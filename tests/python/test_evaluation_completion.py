import json
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "training"))
from score_predictions import score


def test_interrupted_batch_cannot_be_reported_as_completed_evaluation(tmp_path):
    truth = tmp_path / "truth.jsonl"
    predictions = tmp_path / "predictions.jsonl"
    report = tmp_path / "report.json"
    truth.write_text('\n'.join(json.dumps({"sampleId": identity, "defects": []}) for identity in ("a", "b")))
    predictions.write_text(json.dumps({"sampleId": "a", "execution": "Completed", "defects": [], "elapsedMs": 1}))
    with pytest.raises(ValueError, match="population is incomplete"):
        score(truth, predictions, report, False)
    assert not report.exists()


def test_deployment_scores_keep_low_confidence_detections_and_count_failures(tmp_path):
    def defect(class_id, box, confidence=1):
        return {"classId": class_id, "box": box, "score": confidence}

    truth = tmp_path / "truth.jsonl"
    predictions = tmp_path / "predictions.jsonl"
    report = tmp_path / "report.json"
    truths = [
        {"sampleId": "a", "defects": [defect(1, [0, 0, 10, 10]), defect(2, [20, 20, 30, 30])]},
        {"sampleId": "b", "defects": [defect(2, [0, 0, 10, 10]), defect(6, [20, 20, 30, 30])]},
    ]
    emitted = [
        {"sampleId": "a", "execution": "Completed", "elapsedMs": 8, "defects": [
            defect(1, [0, 0, 10, 10], .17), defect(3, [20, 20, 30, 30], .43),
            defect(1, [0, 0, 10, 10], .16)]},
        {"sampleId": "b", "execution": "Failed", "elapsedMs": None,
            "defects": [defect(2, [0, 0, 10, 10])]},
    ]
    truth.write_text('\n'.join(map(json.dumps, truths)), encoding="utf-8")
    predictions.write_text('\n'.join(map(json.dumps, emitted)), encoding="utf-8")
    score(truth, predictions, report, True)
    result = json.loads(report.read_text(encoding="utf-8"))
    assert (result["tp"], result["fp"], result["fn"]) == (1, 2, 3)
    assert result["executionFailures"] == 1
    assert result["precision"] == pytest.approx(1 / 3)
    assert result["recall"] == pytest.approx(1 / 4)
