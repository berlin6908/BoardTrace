"""Export a selected development checkpoint and verify real images in ONNX Runtime."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch

from dataset import DeepPcbDataset
from detection_evaluation import CLASS_NAMES
from model import OnnxDetector, build_model
from train import write_json


def export(args):
    if args.samples < 1:
        raise ValueError("At least one validation comparison image is required")
    args.output.mkdir(parents=True, exist_ok=True)
    if any(args.output.iterdir()):
        raise ValueError("Export output directory must be empty")
    torch.set_num_threads(4)
    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
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
    comparisons = []
    for index, sample in enumerate(dataset.rows):
        inputs = dataset.load_image(index).unsqueeze(0)
        with torch.inference_mode():
            expected = [value.numpy() for value in wrapper(inputs)]
        actual = session.run(None, {"images": inputs.numpy()})
        np.testing.assert_array_equal(expected[1], actual[1])
        np.testing.assert_allclose(expected[0], actual[0], atol=0.1, rtol=1e-4)
        np.testing.assert_allclose(expected[2], actual[2], atol=1e-4, rtol=1e-3)
        comparisons.append({"sampleId": sample["sampleId"], "image": sample["image"],
            "imageSha256": hashlib.sha256((args.data_root / sample["image"]).read_bytes()).hexdigest(),
            "detections": len(actual[1]), "maxBoxDelta": float(np.max(np.abs(expected[0] - actual[0]), initial=0)),
            "maxScoreDelta": float(np.max(np.abs(expected[2] - actual[2]), initial=0)),
            "boxes": actual[0].tolist(), "labels": actual[1].tolist(), "scores": actual[2].tolist()})
    manifest = {"kind": state["identity"]["kind"], "model": "TorchVision FasterRCNN ResNet50 FPN",
        "checkpointEpoch": state["epoch"], "checkpointSha256": hashlib.sha256(args.checkpoint.read_bytes()).hexdigest(),
        "modelFile": model_path.name, "modelSha256": hashlib.sha256(model_path.read_bytes()).hexdigest(),
        "modelSourceSha256": source_hash, "input": {"name": "images", "shape": [1, 3, 640, 640], "dtype": "float32",
            "layout": "NCHW", "color": "RGB", "scale": "pixel / 255", "resize": "640x640 required",
            "normalization": "ImageNet mean/std inside model; do not apply it twice"},
        "outputs": {"boxes": "float32 Nx4 continuous xyxy pixels", "labels": "int64 N class IDs", "scores": "float32 N"},
        "classes": {str(index): name for index, name in enumerate(CLASS_NAMES, 1)},
        "postprocessing": {"nms": "inside ONNX", "confidenceThreshold": state["identity"]["evaluationProtocol"]["confidenceThreshold"],
            "confidenceComparison": ">=", "modelScoreFloor": 0.001, "maximumDetections": 100},
        "opset": 17, "onnxruntime": ort.__version__, "verificationSplit": "validation",
        "verificationSamples": len(comparisons), "qualityApproved": False,
        "validation": state["validation"], "trainingIdentity": state["identity"]}
    write_json(args.output / "comparison.json", comparisons)
    write_json(args.output / "model.manifest.json", manifest)
    print(json.dumps({"modelSha256": manifest["modelSha256"], "verificationSamples": len(comparisons),
                      "maxBoxDelta": max(row["maxBoxDelta"] for row in comparisons),
                      "maxScoreDelta": max(row["maxScoreDelta"] for row in comparisons)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--manifests", type=Path, default=Path("training/manifests"))
    parser.add_argument("--samples", type=int, default=4)
    export(parser.parse_args())
