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
