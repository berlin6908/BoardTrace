import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "training"))
from metrics import iou, match, operating_point


def defect(box, class_id=1, score=1):
    return {"box": box, "classId": class_id, "score": score}


def test_duplicate_predictions_cannot_claim_the_same_defect():
    truth = [defect([0, 0, 10, 10])]
    result = match([defect([0, 0, 10, 10], score=0.8), defect([0, 0, 10, 10], score=0.9)], truth)
    assert result == {"matches": [(1, 0)], "falsePositives": [0], "falseNegatives": []}
    assert operating_point(1, 1, 0)["f1"] == pytest.approx(2 / 3)


def test_wrong_class_is_both_a_miss_and_false_positive_but_localization_matches():
    predictions, truths = [defect([0, 0, 10, 10], 2)], [defect([0, 0, 10, 10], 1)]
    assert match(predictions, truths) == {"matches": [], "falsePositives": [0], "falseNegatives": [0]}
    assert match(predictions, truths, class_aware=False)["matches"] == [(0, 0)]


def test_iou_and_confidence_boundaries_are_inclusive():
    prediction = defect([0, 0, 5, 10], score=0.5)
    truth = defect([0, 0, 10, 10])
    assert iou(prediction["box"], truth["box"]) == 0.5
    assert match([prediction], [truth])["matches"] == [(0, 0)]
    assert not match([{**prediction, "score": 0.4999}], [truth])["matches"]


def test_execution_failure_has_no_predictions_and_all_truths_are_missed():
    assert match([], [defect([0, 0, 10, 10]), defect([20, 20, 30, 30])])["falseNegatives"] == [0, 1]
    assert operating_point(0, 0, 2) == {"tp": 0, "fp": 0, "fn": 2, "precision": 0, "recall": 0, "f1": 0}
