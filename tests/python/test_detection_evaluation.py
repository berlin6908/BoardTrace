"""Hand-computable populations exercise COCO AP and deployment reporting together."""
import json
from copy import deepcopy
from pathlib import Path
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "training"))
from detection_evaluation import CLASS_NAMES, evaluate_predictions


@pytest.fixture
def config():
    return json.loads((ROOT / "training/evaluation-config.json").read_text(encoding="utf-8"))


def defect(class_id=1, box=(0, 0, 10, 10), score=0.9):
    return {"classId": class_id, "box": list(box), "score": score}


def prediction(sample_id, defects, execution="Completed"):
    return {"sampleId": sample_id, "execution": execution, "defects": defects, "elapsedMs": 12.0}


def test_class_deployment_threshold_changes_pr_and_confusion_but_not_raw_ap(config):
    truths = [{"sampleId": "all-classes", "defects": [
        defect(index, (20 * index, 0, 20 * index + 10, 10)) for index in range(1, 7)]},
        {"sampleId": "failed", "defects": [defect(5)]}]
    true_boxes = [{**item, "score": 0.2} for item in truths[0]["defects"]]
    wrong_class = {**true_boxes[0], "classId": 2, "score": 0.9}
    duplicate = {**true_boxes[2], "score": 0.25}
    rows = [prediction("all-classes", [*true_boxes, wrong_class, duplicate]),
        prediction("failed", [defect(5, score=0.99)], "Failed")]
    untouched = deepcopy(rows)
    permissive = evaluate_predictions(truths, rows, {**config, "scoreThresholds": [0.1] * 6})
    stricter = evaluate_predictions(truths, rows, {**config, "scoreThresholds": [0.1, 0.1, 0.3, 0.1, 0.1, 0.1]})

    assert permissive["operatingPoint"] == {"tp": 6, "fp": 2, "fn": 1,
        "precision": 6 / 8, "recall": 6 / 7, "f1": pytest.approx(0.8)}
    assert stricter["operatingPoint"]["tp"] == 5
    assert stricter["operatingPoint"]["fp"] == 1
    assert stricter["operatingPoint"]["fn"] == 2
    assert [permissive["classes"]["mousebite"][key] for key in ("tp", "fp", "fn")] == [1, 1, 0]
    assert [stricter["classes"]["mousebite"][key] for key in ("tp", "fp", "fn")] == [0, 0, 1]
    assert permissive["confusionMatrix"]["matrix"][3][3] == 1
    assert permissive["confusionMatrix"]["matrix"][0][3] == 1
    assert stricter["confusionMatrix"]["matrix"][3][0] == 1
    assert stricter["confusionMatrix"]["matrix"][0][3] == 0
    for report in (permissive, stricter):
        assert report["confusionMatrix"]["matrix"][1][2] == 1
        assert report["images"] == 2 and report["groundTruthDefects"] == 7
        assert report["executionFailures"] == 1 and report["failedSamples"] == ["failed"]
    for metric in ("map50", "map50To95"):
        assert permissive[metric] == stricter[metric]
    for name in CLASS_NAMES:
        assert permissive["classes"][name]["ap50"] == stricter["classes"][name]["ap50"]
        assert permissive["classes"][name]["ap50To95"] == stricter["classes"][name]["ap50To95"]
    assert rows == untouched


def test_perfect_predictions_have_unit_ap_and_counts_for_all_six_classes(config):
    truths = [{"sampleId": str(index), "defects": [defect(index)]} for index in range(1, 7)]
    rows = [prediction(row["sampleId"], row["defects"]) for row in reversed(truths)]
    report = evaluate_predictions(truths, rows, config)

    assert report["map50"] == pytest.approx(1)
    assert report["map50To95"] == pytest.approx(1)
    assert report["operatingPoint"] == {"tp": 6, "fp": 0, "fn": 0, "precision": 1, "recall": 1, "f1": 1}
    assert report["images"] == report["executionCompleted"] == report["groundTruthDefects"] == 6
    assert report["executionFailures"] == 0
    for index, name in enumerate(CLASS_NAMES, start=1):
        assert report["classes"][name] == {"classId": index, "tp": 1, "fp": 0, "fn": 0,
            "precision": 1, "recall": 1, "f1": 1, "ap50": pytest.approx(1), "ap50To95": pytest.approx(1)}
        assert report["confusionMatrix"]["matrix"][index][index] == 1
    json.dumps(report, allow_nan=False)


def test_each_class_threshold_is_inclusive_and_raising_one_does_not_filter_other_classes(config):
    thresholds = [0.17, 0.22, 0.90, 0.68, 0.43, 0.59]
    truths = [{"sampleId": str(index), "defects": [defect(index)]} for index in range(1, 7)]
    rows = [prediction(str(index), [defect(index, score=thresholds[index - 1])]) for index in range(1, 7)]
    baseline = evaluate_predictions(truths, rows, {**config, "scoreThresholds": thresholds})
    assert baseline["operatingPoint"]["tp"] == 6
    for class_index, name in enumerate(CLASS_NAMES):
        raised = thresholds.copy()
        raised[class_index] += 0.01
        report = evaluate_predictions(truths, rows, {**config, "scoreThresholds": raised})
        assert report["operatingPoint"]["tp"] == 5
        assert report["classes"][name]["fn"] == 1
        assert all(report["classes"][other]["tp"] == 1 for other in CLASS_NAMES if other != name)
        assert report["map50"] == baseline["map50"]


@pytest.mark.parametrize("thresholds", [0.5, [0.5] * 5, [0.5] * 7, [0.5, 0.5, float("nan"), 0.5, 0.5, 0.5],
    [0.5, 0.5, float("inf"), 0.5, 0.5, 0.5], [-0.1] * 6, [1.1] * 6])
def test_incomplete_or_invalid_deployment_thresholds_cannot_produce_a_report(config, thresholds):
    truths = [{"sampleId": "a", "defects": [defect()]}]
    with pytest.raises(ValueError, match="scoreThresholds"):
        evaluate_predictions(truths, [prediction("a", [defect()])], {**config, "scoreThresholds": thresholds})


def test_wrong_class_duplicates_and_failed_execution_keep_all_denominators(config):
    spur = defect(4, (20, 20, 30, 30))
    truths = [{"sampleId": "a", "defects": [defect(1), spur]},
        {"sampleId": "b", "defects": [defect(5)]}, {"sampleId": "c", "defects": [defect(3)]}]
    rows = [prediction("a", [defect(2), spur, {**spur, "score": 0.8}, defect(6, (50, 50, 60, 60))]),
        prediction("b", [defect(5)], "Failed"), prediction("c", [])]
    report = evaluate_predictions(truths, rows, config)

    assert report["operatingPoint"] == {"tp": 1, "fp": 3, "fn": 3,
        "precision": 0.25, "recall": 0.25, "f1": 0.25}
    assert report["images"] == 3
    assert report["groundTruthDefects"] == 4
    assert report["executionCompleted"] == 2
    assert report["executionFailures"] == 1
    assert report["failedSamples"] == ["b"]
    assert [(report["classes"][name]["tp"], report["classes"][name]["fp"], report["classes"][name]["fn"])
        for name in CLASS_NAMES] == [(0, 0, 1), (0, 1, 0), (0, 0, 1), (1, 1, 0), (0, 0, 1), (0, 1, 0)]
    assert report["classes"]["short"]["ap50"] is None
    assert report["classes"]["copper"]["ap50"] == 0
    assert report["map50"] == pytest.approx(0.25)
    expected = [[0] * 7 for _ in range(7)]
    for true_class, predicted_class in [(1, 2), (4, 4), (0, 4), (0, 6), (5, 0), (3, 0)]:
        expected[true_class][predicted_class] = 1
    assert report["confusionMatrix"]["matrix"] == expected


def test_empty_predictions_are_zero_ap_and_every_truth_is_missed(config):
    truths = [{"sampleId": "empty", "defects": [defect(index) for index in range(1, 7)]}]
    report = evaluate_predictions(truths, [prediction("empty", [])], config)
    assert report["map50"] == report["map50To95"] == 0
    assert report["operatingPoint"] == {"tp": 0, "fp": 0, "fn": 6, "precision": 0, "recall": 0, "f1": 0}
    assert all(row["ap50"] == row["ap50To95"] == 0 for row in report["classes"].values())
    assert all(report["confusionMatrix"]["matrix"][index][0] == 1 for index in range(1, 7))


@pytest.mark.parametrize(("score", "expected_ap"), [(0.001, 1), (0.0009, 0)])
def test_ap_floor_is_inclusive_and_separate_from_the_deployment_threshold(config, score, expected_ap):
    truths = [{"sampleId": "floor", "defects": [defect()]}]
    report = evaluate_predictions(truths, [prediction("floor", [defect(score=score)])], config)
    assert report["map50"] == pytest.approx(expected_ap)
    assert report["map50To95"] == pytest.approx(expected_ap)
    assert report["operatingPoint"]["tp"] == 0
    assert report["operatingPoint"]["fn"] == 1


def test_ap_uses_the_highest_100_detections_across_classes_per_image(config):
    truths = [{"sampleId": "cap", "defects": [defect()]}]
    rows = [prediction("cap", [defect(2, score=0.9) for _ in range(100)] + [defect(score=0.8)])]
    report = evaluate_predictions(truths, rows, config)
    assert report["map50"] == report["map50To95"] == 0
    assert report["operatingPoint"]["tp"] == 1
    assert report["operatingPoint"]["fp"] == 100


def test_half_overlap_counts_at_ap50_but_not_at_stricter_iou_thresholds(config):
    truths = [{"sampleId": "boundary", "defects": [defect()]}]
    rows = [prediction("boundary", [defect(box=(0, 0, 5, 10), score=0.5)])]
    report = evaluate_predictions(truths, rows, config)
    assert report["operatingPoint"]["tp"] == 1
    assert report["map50"] == pytest.approx(1)
    assert report["map50To95"] == pytest.approx(0.1)


@pytest.mark.parametrize("case", ["missing", "unexpected", "duplicate prediction", "duplicate truth", "empty"])
def test_population_must_be_complete_and_unique(config, case):
    truths = [{"sampleId": "a", "defects": [defect()]}]
    rows = [prediction("a", [])]
    if case == "missing":
        rows = []
    elif case == "unexpected":
        rows.append(prediction("other", []))
    elif case == "duplicate prediction":
        rows += rows
    elif case == "duplicate truth":
        truths += truths
    else:
        truths, rows = [], []
    with pytest.raises(ValueError, match="population|Duplicate"):
        evaluate_predictions(truths, rows, config)


@pytest.mark.parametrize("kind", ["truth", "completed", "failed", "below floor", "execution"])
def test_unknown_classes_and_execution_states_cannot_be_silently_excluded(config, kind):
    truths = [{"sampleId": "a", "defects": [defect()]}]
    rows = [prediction("a", [])]
    if kind == "truth":
        truths[0]["defects"] = [defect(7)]
    elif kind == "execution":
        rows[0]["execution"] = "Interrupted"
    else:
        rows[0]["defects"] = [defect(7, score=0.0001 if kind == "below floor" else 0.9)]
        if kind == "failed":
            rows[0]["execution"] = "Failed"
    with pytest.raises(ValueError, match="Unknown"):
        evaluate_predictions(truths, rows, config)
