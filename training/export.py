"""Export a selected development checkpoint and verify real images in ONNX Runtime."""
import argparse
import hashlib
import json
from pathlib import Path
from unittest.mock import patch

import numpy as np
import onnx
import onnxruntime as ort
import torch
from torchvision.ops import boxes as box_ops

from dataset import DeepPcbDataset
from detection_evaluation import CLASS_NAMES, deployment_predictions, evaluate_predictions
from metrics import match
from model import OnnxDetector, build_model
from train import write_json


# TorchVision 0.22.1 uses this branch for GPU detection and ONNX tracing, while
# eager CPU switches large inputs to per-class NMS. Float32 offsets can change
# suppression at an IoU boundary. Scope the same GPU branch to this command;
# training and ordinary CPU inference keep their existing behavior.
@patch.object(box_ops, "batched_nms", box_ops._batched_nms_coordinate_trick)
def export(args):
    if args.samples < 1:
        raise ValueError("At least one validation comparison image is required")
    args.output.mkdir(parents=True, exist_ok=True)
    if any(args.output.iterdir()):
        raise ValueError("Export output directory must be empty")
    torch.set_num_threads(4)
    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    protocol = state["identity"]["evaluationProtocol"]
    score_thresholds = np.asarray(protocol["scoreThresholds"])
    for filename in ("model.py", "dataset.py"):
        current_hash = hashlib.sha256(Path(__file__).with_name(filename).read_bytes()).hexdigest()
        if state["identity"]["sourceSha256"][filename] != current_hash:
            raise ValueError(f"Model/preprocessing source differs from the selected checkpoint: {filename}")
    source_hash = state["identity"]["sourceSha256"]["model.py"]
    model = build_model(pretrained=False)
    model.load_state_dict(state["model"])
    wrapper = OnnxDetector(model.eval()).eval()
    dataset = DeepPcbDataset(args.data_root, args.manifests, "validation", limit=args.samples)
    for key in ("inputsSha256", "truthSha256"):
        if dataset.identity()[key] != state["identity"]["data"]["validation"][key]:
            raise ValueError("Validation manifests differ from the selected checkpoint")
    model_path = args.output / "detector.onnx"
    example = dataset.load_image(0).unsqueeze(0)
    torch.onnx.export(wrapper, example, str(model_path), input_names=["images"],
        output_names=["boxes", "labels", "scores"], opset_version=17, dynamo=False,
        dynamic_axes={"boxes": {0: "detections"}, "labels": {0: "detections"}, "scores": {0: "detections"}})
    onnx.checker.check_model(str(model_path))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
    comparisons, reference_predictions, onnx_predictions = [], [], []
    for index, sample in enumerate(dataset.rows):
        inputs = dataset.load_image(index).unsqueeze(0)
        with torch.inference_mode():
            expected = [value.numpy() for value in wrapper(inputs)]
        actual = session.run(None, {"images": inputs.numpy()})
        reference_row = prediction_row(sample["sampleId"], expected)
        onnx_row = prediction_row(sample["sampleId"], actual)
        try:
            np.testing.assert_array_equal(expected[1], actual[1])
            np.testing.assert_allclose(expected[0], actual[0], atol=0.1, rtol=1e-4)
            np.testing.assert_allclose(expected[2], actual[2], atol=1e-4, rtol=1e-3)
            np.testing.assert_array_equal(expected[2] >= score_thresholds[expected[1] - 1],
                                          actual[2] >= score_thresholds[actual[1] - 1])
            reference_matches = match(deployment_predictions(reference_row["defects"], protocol["scoreThresholds"]),
                dataset.truths[index]["defects"], protocol["iouThreshold"], confidence=0)
            actual_matches = match(deployment_predictions(onnx_row["defects"], protocol["scoreThresholds"]),
                dataset.truths[index]["defects"], protocol["iouThreshold"], confidence=0)
            assert reference_matches == actual_matches, "Deployment matching differs"
        except AssertionError as error:
            write_json(args.output / "comparison-failure.json", {"sampleId": sample["sampleId"],
                "validationIndex": index, "error": str(error), "reference": reference_row, "onnx": onnx_row})
            raise RuntimeError(f"ONNX comparison failed for validation sample {sample['sampleId']}") from error
        reference_predictions.append(reference_row)
        onnx_predictions.append(onnx_row)
        comparisons.append({"sampleId": sample["sampleId"], "image": sample["image"], "reference": sample["reference"],
            "imageSha256": hashlib.sha256((args.data_root / sample["image"]).read_bytes()).hexdigest(),
            "referenceSha256": hashlib.sha256((args.data_root / sample["reference"]).read_bytes()).hexdigest(),
            "detections": len(actual[1]), "maxBoxDelta": float(np.max(np.abs(expected[0] - actual[0]), initial=0)),
            "maxScoreDelta": float(np.max(np.abs(expected[2] - actual[2]), initial=0)),
            "boxes": actual[0].tolist(), "labels": actual[1].tolist(), "scores": actual[2].tolist()})
        print(json.dumps({"comparisonSample": sample["sampleId"], "completed": index + 1,
                          "detections": len(actual[1]), "deploymentMatching": "identical"}), flush=True)
    reference_report = evaluate_predictions(dataset.truths, reference_predictions, protocol)
    onnx_report = evaluate_predictions(dataset.truths, onnx_predictions, protocol)
    write_json(args.output / "reference-validation.json", reference_report)
    write_json(args.output / "onnx-validation.json", onnx_report)
    manifest = {"kind": state["identity"]["kind"], "model": "TorchVision FasterRCNN ResNet50 FPN",
        "checkpointEpoch": state["epoch"], "checkpointSha256": hashlib.sha256(args.checkpoint.read_bytes()).hexdigest(),
        "modelFile": model_path.name, "modelSha256": hashlib.sha256(model_path.read_bytes()).hexdigest(),
        "modelSourceSha256": source_hash, "exportSourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "referenceNms": "TorchVision coordinate trick, matching GPU and ONNX tracing; export process only",
        "input": {"name": "images", "shape": [1, 3, 640, 640], "dtype": "float32",
            "layout": "NCHW", "channels": ["tested grayscale", "reference grayscale", "absolute grayscale difference"],
            "scale": "each 8-bit channel / 255", "resize": "640x640 aligned pair required",
            "normalization": "each channel (value - 0.5) / 0.5 inside model; do not apply it twice"},
        "outputs": {"boxes": "float32 Nx4 continuous xyxy pixels", "labels": "int64 N class IDs", "scores": "float32 N"},
        "classes": {str(index): name for index, name in enumerate(CLASS_NAMES, 1)},
        "postprocessing": {"nms": "inside ONNX", "scoreThresholds": protocol["scoreThresholds"],
            "confidenceComparison": ">=", "modelScoreFloor": 0.001, "maximumDetections": 100},
        "opset": 17, "onnxruntime": ort.__version__, "verificationSplit": "validation",
        "verificationSamples": len(comparisons), "qualityApproved": False,
        "validation": state["validation"], "trainingIdentity": state["identity"]}
    write_json(args.output / "comparison.json", comparisons)
    write_json(args.output / "model.manifest.json", manifest)
    print(json.dumps({"modelSha256": manifest["modelSha256"], "verificationSamples": len(comparisons),
                      "maxBoxDelta": max(row["maxBoxDelta"] for row in comparisons),
                      "maxScoreDelta": max(row["maxScoreDelta"] for row in comparisons)}))


def prediction_row(sample_id, outputs):
    return {"sampleId": sample_id, "execution": "Completed", "defects": [
        {"classId": int(label), "box": box.tolist(), "score": float(score)}
        for box, label, score in zip(*outputs)]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--manifests", type=Path, default=Path("training/manifests"))
    parser.add_argument("--samples", type=int, default=4)
    export(parser.parse_args())
