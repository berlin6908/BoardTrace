"""Real optimizer steps, ONNX export and runtime comparison. Not a quality evaluation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import time

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import torch
import torchvision

from model import OnnxDetector, build_model


def main(output, device):
    torch.manual_seed(20260913)
    torch.set_num_threads(4)
    output.mkdir(parents=True, exist_ok=True)
    manifest = [json.loads(line) for line in Path("training/manifests/inputs/train.jsonl").read_text().splitlines()]
    labels = {row["sampleId"]: row["defects"] for row in map(json.loads, Path("training/manifests/truth/train.jsonl").read_text().splitlines())}
    samples = manifest[:2]
    images, targets = [], []
    for sample in samples:
        image = cv2.imdecode(np.fromfile(Path("data") / sample["image"], dtype=np.uint8), cv2.IMREAD_COLOR)
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        images.append(torch.from_numpy(rgb.copy()).permute(2, 0, 1).float().div(255).to(device))
        defects = labels[sample["sampleId"]]
        targets.append({"boxes": torch.tensor([d["box"] for d in defects], dtype=torch.float32, device=device),
                        "labels": torch.tensor([d["classId"] for d in defects], dtype=torch.int64, device=device)})
    model = build_model(pretrained=True).to(device).train()
    optimizer = torch.optim.SGD([p for p in model.parameters() if p.requires_grad], lr=0.002, momentum=0.9, weight_decay=0.0005)
    losses = []
    started = time.perf_counter()
    for step in range(4):
        terms = model(images, targets)
        loss = sum(terms.values())
        if not torch.isfinite(loss):
            raise RuntimeError("Non-finite training loss")
        optimizer.zero_grad()
        loss.backward()
        optimizer.step()
        losses.append(float(loss.detach()))
        print(json.dumps({"step": step, "loss": losses[-1]}), flush=True)
    train_seconds = time.perf_counter() - started
    model.cpu().eval()
    wrapper = OnnxDetector(model).eval()
    example = images[0].detach().cpu().unsqueeze(0)
    model_path = output / "probe.onnx"
    torch.onnx.export(wrapper, example, str(model_path), input_names=["images"],
        output_names=["boxes", "labels", "scores"], opset_version=17, dynamo=False,
        dynamic_axes={"boxes": {0: "detections"}, "labels": {0: "detections"}, "scores": {0: "detections"}})
    onnx.checker.check_model(str(model_path))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
    with torch.no_grad():
        expected = [v.numpy() for v in wrapper(example)]
    actual = session.run(None, {"images": example.numpy()})
    np.testing.assert_array_equal(expected[1], actual[1])
    np.testing.assert_allclose(expected[0], actual[0], atol=0.1, rtol=1e-4)
    np.testing.assert_allclose(expected[2], actual[2], atol=1e-4, rtol=1e-3)
    report = {"kind": "environment-probe-not-quality-evaluation", "python": platform.python_version(),
        "torch": torch.__version__, "torchvision": torchvision.__version__, "onnxruntime": ort.__version__,
        "device": device, "gpu": torch.cuda.get_device_name(0) if device == "cuda" else None,
        "host": platform.node(), "slurmJobId": os.getenv("SLURM_JOB_ID"),
        "samples": [s["sampleId"] for s in samples], "optimizerSteps": len(losses), "losses": losses,
        "trainingSeconds": train_seconds, "modelSha256": hashlib.sha256(model_path.read_bytes()).hexdigest(),
        "imageSha256": hashlib.sha256((Path("data") / samples[0]["image"]).read_bytes()).hexdigest(),
        "pytorchOnnxMaxBoxDelta": float(np.max(np.abs(expected[0] - actual[0]), initial=0)),
        "pytorchOnnxMaxScoreDelta": float(np.max(np.abs(expected[2] - actual[2]), initial=0)),
        "input": samples[0]["image"], "boxes": actual[0].reshape(-1).tolist(),
        "labels": actual[1].tolist(), "scores": actual[2].tolist()}
    (output / "probe.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k not in ("boxes", "labels", "scores")}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path("artifacts/environment/training-probe"))
    parser.add_argument("--device", choices=["cuda", "cpu"], default="cuda")
    args = parser.parse_args()
    main(args.output, args.device)
