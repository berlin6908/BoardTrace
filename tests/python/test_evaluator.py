"""Run the actual C# batch process against controlled input and a broken manifest."""
import json
from pathlib import Path
import subprocess

import cv2
import numpy as np
import pytest

ROOT = Path(__file__).resolve().parents[2]
DOTNET = ROOT / ".local/dotnet/dotnet.exe"
EVALUATOR = ROOT / "tools/BoardTrace.Evaluate/bin/Release/net10.0/BoardTrace.Evaluate.dll"


@pytest.fixture
def inputs(tmp_path):
    image = np.full((640, 640), 255, dtype=np.uint8)
    cv2.rectangle(image, (60, 70), (400, 140), 0, -1)
    cv2.rectangle(image, (330, 70), (400, 500), 0, -1)
    cv2.imencode(".png", image)[1].tofile(tmp_path / "reference.png")
    (tmp_path / "corrupt.png").write_bytes(b"invalid image")
    return {"sampleId": "controlled-normal", "image": "reference.png", "reference": "reference.png"}


def run(tmp_path):
    return subprocess.run([str(DOTNET), str(EVALUATOR),
        "--manifest", str(tmp_path / "inputs.jsonl"), "--data-root", str(tmp_path),
        "--recipe", str(ROOT / "training/classical-development.json"),
        "--output", str(tmp_path / "results.jsonl")], capture_output=True, text=True, encoding="utf-8")


def test_bad_image_is_recorded_and_next_input_still_runs(tmp_path, inputs):
    broken = {**inputs, "sampleId": "corrupt", "image": "corrupt.png"}
    (tmp_path / "inputs.jsonl").write_text(json.dumps(broken) + "\n" + json.dumps(inputs), encoding="utf-8")
    process = run(tmp_path)
    assert process.returncode == 0, process.stderr
    rows = [json.loads(line) for line in (tmp_path / "results.jsonl").read_text(encoding="utf-8").splitlines()]
    assert [(row["sampleId"], row["execution"], row["decision"]) for row in rows] == [
        ("corrupt", "Failed", "NotEvaluated"), ("controlled-normal", "Completed", "Pass")]


def test_broken_manifest_does_not_publish_a_partial_evaluation(tmp_path, inputs):
    (tmp_path / "inputs.jsonl").write_text(json.dumps(inputs) + "\n{" , encoding="utf-8")
    process = run(tmp_path)
    assert process.returncode != 0
    assert not (tmp_path / "results.jsonl").exists()
    assert (tmp_path / "results.jsonl.partial").exists()
