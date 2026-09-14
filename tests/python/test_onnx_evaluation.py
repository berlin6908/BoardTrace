"""Exercise the frozen-model CLI with a real pixel-dependent ORT graph."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import cv2
import numpy as np
import onnx
import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "training"))
import evaluate_onnx


def write_rows(path, rows):
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")


@pytest.fixture
def case(tmp_path):
    model = ROOT / "tests/BoardTrace.Vision.Tests/Fixtures/pixel-detector.onnx"
    for name, first, second in (("tested", 64, 200), ("reference", 192, 32)):
        image = np.zeros((640, 640), dtype=np.uint8)
        image[:, 320:] = 255
        image[0, :2] = [first, second]
        (tmp_path / f"{name}.png").write_bytes(cv2.imencode(".png", image)[1].tobytes())
    row = {"sampleId": "a", "image": "tested.png", "reference": "reference.png",
        "imageSha256": hashlib.sha256((tmp_path / "tested.png").read_bytes()).hexdigest(),
        "referenceSha256": hashlib.sha256((tmp_path / "reference.png").read_bytes()).hexdigest()}
    truth = {"sampleId": "a", "defects": [
        {"classId": index, "box": [10.25, 20.5, 30.25, 50.5]} for index in range(1, 7)]}
    write_rows(tmp_path / "inputs.jsonl", [row, {**row, "sampleId": "b"}])
    write_rows(tmp_path / "truth.jsonl", [truth, {**truth, "sampleId": "b"}])
    config = json.loads((ROOT / "training/evaluation-config.json").read_text())
    config["scoreThresholds"] = [float(np.float32(64) / np.float32(255)), 0.8, 0.6, 0.9, 0.2, 0.7]
    (tmp_path / "protocol.json").write_text(json.dumps(config))
    args = argparse.Namespace(model=model, model_sha256=hashlib.sha256(model.read_bytes()).hexdigest(),
        inputs=tmp_path / "inputs.jsonl", data_root=tmp_path, truth=tmp_path / "truth.jsonl",
        evaluation_config=tmp_path / "protocol.json", output=tmp_path / "run")
    return args, row, truth


def test_real_session_reused_with_exact_paired_planes_and_raw_ap(case, monkeypatch):
    args, _, _ = case
    original = evaluate_onnx.ort.InferenceSession
    sessions = []
    def create(*positional, **keywords):
        session = original(*positional, **keywords)
        sessions.append(session)
        return session
    monkeypatch.setattr(evaluate_onnx.ort, "InferenceSession", create)
    assert evaluate_onnx.run_evaluation(args) == 0
    assert len(sessions) == 1
    rows = [json.loads(line) for line in (args.output / "raw-predictions.jsonl").read_text().splitlines()]
    scores = np.array([64, 192, 128, 200, 32, 168], dtype=np.float32) / np.float32(255)
    assert len(rows) == 2
    for row in rows:
        np.testing.assert_array_equal([d["score"] for d in row["defects"]], scores)
        assert [d["classId"] for d in row["defects"]] == list(range(1, 7))
    report = json.loads((args.output / "report.json").read_text())
    assert [report["operatingPoint"][key] for key in ("tp", "fp", "fn")] == [2, 0, 10]
    assert report["map50"] == pytest.approx(1)
    assert report["map50To95"] == pytest.approx(1)
    run = json.loads((args.output / "run.json").read_text())
    assert run["timing"]["warmSamples"] == 1
    assert run["timing"]["coldDetectMs"] == rows[0]["elapsedMs"]


@pytest.mark.parametrize("failure", ["corrupt-reference", "changed-tested", "wrong-size", "no-contrast"])
def test_failed_image_keeps_truth_population_and_later_sample_runs(case, failure):
    args, row, truth = case
    if failure == "changed-tested":
        bad = {**row, "sampleId": "bad", "imageSha256": "0" * 64}
    else:
        raw = b"invalid image"
        if failure in ("wrong-size", "no-contrast"):
            size = 32 if failure == "wrong-size" else 640
            raw = cv2.imencode(".png", np.zeros((size, size), dtype=np.uint8))[1].tobytes()
        (args.data_root / "bad.png").write_bytes(raw)
        bad = {**row, "sampleId": "bad", "reference": "bad.png",
            "referenceSha256": hashlib.sha256(raw).hexdigest()}
    write_rows(args.inputs, [bad, row])
    write_rows(args.truth, [{**truth, "sampleId": "bad"}, truth])
    assert evaluate_onnx.run_evaluation(args) == 1
    rows = [json.loads(line) for line in (args.output / "raw-predictions.jsonl").read_text().splitlines()]
    assert [row["execution"] for row in rows] == ["Failed", "Completed"]
    assert rows[0]["defects"] == [] and rows[0]["error"]
    assert rows[0]["imageSha256"] == row["imageSha256"]
    report = json.loads((args.output / "report.json").read_text())
    assert report["failedSamples"] == ["bad"]
    assert [report["operatingPoint"][key] for key in ("tp", "fp", "fn")] == [1, 0, 11]


def test_truth_mismatch_cannot_publish_report_but_keeps_raw_predictions(case):
    args, _, truth = case
    write_rows(args.truth, [truth])
    with pytest.raises(ValueError, match="population mismatch"):
        evaluate_onnx.run_evaluation(args)
    assert (args.output / "raw-predictions.jsonl").exists()
    assert not (args.output / "report.json").exists()
    assert json.loads((args.output / "run.json").read_text())["state"] == "Failed"


@pytest.mark.parametrize("model_error", ["wrong-sha", "missing", "corrupt"])
def test_cli_bad_model_fails_and_rerun_never_overwrites_prior_evidence(case, model_error):
    args, _, _ = case
    model, digest = args.model, "0" * 64
    if model_error != "wrong-sha":
        model = args.data_root / "invalid.onnx"
        if model_error == "corrupt":
            model.write_bytes(b"not an ONNX graph")
            digest = hashlib.sha256(model.read_bytes()).hexdigest()
    command = [sys.executable, str(ROOT / "training/evaluate_onnx.py"),
        "--model", str(model), "--model-sha256", digest,
        "--inputs", str(args.inputs), "--data-root", str(args.data_root), "--truth", str(args.truth),
        "--evaluation-config", str(args.evaluation_config), "--output", str(args.output)]
    process = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    assert process.returncode != 0
    if model_error == "wrong-sha":
        assert "SHA-256" in process.stderr
    assert not (args.output / "raw-predictions.jsonl").exists()
    original = (args.output / "run.json").read_bytes()
    with pytest.raises(FileExistsError):
        evaluate_onnx.run_evaluation(args)
    assert (args.output / "run.json").read_bytes() == original


def test_invalid_model_outputs_are_execution_failures_not_silently_filtered(case):
    args, _, _ = case
    graph = onnx.load(args.model)
    for tensor in graph.graph.initializer:
        if tensor.name == "fixed_labels":
            tensor.int64_data[0] = 7
    args.model = args.data_root / "unknown-class.onnx"
    onnx.save(graph, args.model)
    args.model_sha256 = hashlib.sha256(args.model.read_bytes()).hexdigest()
    assert evaluate_onnx.run_evaluation(args) == 1
    rows = [json.loads(line) for line in (args.output / "raw-predictions.jsonl").read_text().splitlines()]
    assert all(row["execution"] == "Failed" and row["defects"] == [] for row in rows)
    report = json.loads((args.output / "report.json").read_text())
    assert [report["operatingPoint"][key] for key in ("tp", "fp", "fn")] == [0, 0, 12]


def test_old_scalar_protocol_rejected_before_any_prediction(case):
    args, _, _ = case
    protocol = json.loads(args.evaluation_config.read_text())
    del protocol["scoreThresholds"]
    protocol["confidenceThreshold"] = 0.5
    args.evaluation_config.write_text(json.dumps(protocol))
    with pytest.raises(ValueError, match="scoreThresholds"):
        evaluate_onnx.run_evaluation(args)
    assert not (args.output / "raw-predictions.jsonl").exists()
