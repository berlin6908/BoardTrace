"""Run the actual C# batch process against controlled input and a broken manifest."""
import json
import hashlib
from pathlib import Path
import subprocess
import shutil

import cv2
import numpy as np
import pytest

ROOT = Path(__file__).resolve().parents[2]
DOTNET = shutil.which("dotnet")
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


def test_paired_onnx_requires_reference_and_records_its_actual_sha(tmp_path, inputs):
    missing = {**inputs, "sampleId": "missing-reference", "reference": None}
    corrupt = {**inputs, "sampleId": "corrupt-reference", "reference": "corrupt.png"}
    (tmp_path / "inputs.jsonl").write_text("\n".join(json.dumps(row) for row in (missing, corrupt, inputs)) + "\n", encoding="utf-8")
    model = ROOT / "tests/BoardTrace.Vision.Tests/Fixtures/pixel-detector.onnx"
    process = subprocess.run([str(DOTNET), str(EVALUATOR),
        "--manifest", str(tmp_path / "inputs.jsonl"), "--data-root", str(tmp_path),
        "--model", str(model), "--model-sha256", hashlib.sha256(model.read_bytes()).hexdigest(),
        "--score-thresholds", "0.5,0.5,0.5,0.5,0.5,0.5", "--output", str(tmp_path / "results.jsonl")],
        capture_output=True, text=True, encoding="utf-8")
    assert process.returncode == 0, process.stderr
    rows = [json.loads(line) for line in (tmp_path / "results.jsonl").read_text().splitlines()]
    assert [row["execution"] for row in rows] == ["Failed", "Failed", "Completed"]
    assert [row["decision"] for row in rows[:2]] == ["NotEvaluated", "NotEvaluated"]
    expected_sha = hashlib.sha256((tmp_path / "reference.png").read_bytes()).hexdigest()
    assert rows[-1]["imageSha256"] == rows[-1]["referenceSha256"] == expected_sha
    assert [defect["classId"] for defect in rows[-1]["defects"]] == [1, 2, 4, 5]
    assert json.loads(process.stdout)["scoreThresholds"] == [0.5] * 6


@pytest.mark.parametrize("flag,value", [("--score-threshold", "0.5"), ("--score-thresholds", "0.5,0.5")])
def test_onnx_rejects_the_removed_scalar_cli_and_incomplete_class_vector(tmp_path, flag, value):
    process = subprocess.run([str(DOTNET), str(EVALUATOR), "--model", "unused.onnx", "--model-sha256", "0" * 64,
        flag, value, "--manifest", "unused.jsonl", "--data-root", str(tmp_path), "--output", str(tmp_path / "results.jsonl")],
        capture_output=True, text=True, encoding="utf-8")
    assert process.returncode != 0
    assert not (tmp_path / "results.jsonl").exists()
