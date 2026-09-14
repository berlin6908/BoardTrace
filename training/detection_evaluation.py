"""Six-class evaluation over a complete, caller-supplied sample population."""
from contextlib import redirect_stdout
from io import StringIO

import numpy as np
from pycocotools.coco import COCO
from pycocotools.cocoeval import COCOeval

from metrics import match, operating_point


CLASS_NAMES = ("open", "short", "mousebite", "spur", "copper", "pin-hole")


def deployment_predictions(defects, score_thresholds):
    """Keep the deployment set in original order; class IDs are 1 through 6."""
    thresholds = np.asarray(score_thresholds, dtype=float)
    if thresholds.shape != (6,) or not np.all(np.isfinite(thresholds)) or np.any((thresholds < 0) | (thresholds > 1)):
        raise ValueError("scoreThresholds must contain six finite values in [0, 1], in class order 1 through 6")
    return [defect for defect in defects if defect["score"] >= thresholds[defect["classId"] - 1]]


def _index_rows(rows, kind):
    indexed = {}
    for row in rows:
        sample_id = row["sampleId"]
        if sample_id in indexed:
            raise ValueError(f"Duplicate {kind} sample ID: {sample_id}")
        for defect in row["defects"]:
            if type(defect["classId"]) is not int or not 1 <= defect["classId"] <= len(CLASS_NAMES):
                raise ValueError(f"Unknown class ID in {kind} sample {sample_id}: {defect['classId']}")
        indexed[sample_id] = row
    return indexed


def _bbox(defect):
    x1, y1, x2, y2 = defect["box"]
    return [x1, y1, x2 - x1, y2 - y1]


def _mean_ap(precision):
    defined = precision[precision >= 0]
    return float(defined.mean()) if defined.size else None


def _coco_ap(truths, predictions, config):
    thresholds = np.asarray(config["mapIoUThresholds"], dtype=float)
    if thresholds.shape != (10,) or not np.allclose(thresholds, np.linspace(0.5, 0.95, 10)):
        raise ValueError("mAP50:95 requires IoU thresholds 0.50 through 0.95 in steps of 0.05")
    images, annotations, detections = [], [], []
    max_detections = config["maxDetectionsPerImage"]
    for image_id, (sample_id, truth) in enumerate(truths.items(), start=1):
        images.append({"id": image_id})
        for defect in truth["defects"]:
            box = _bbox(defect)
            annotations.append({"id": len(annotations) + 1, "image_id": image_id,
                "category_id": defect["classId"], "bbox": box,
                "area": box[2] * box[3], "iscrowd": 0})
        # The protocol caps the whole image, including predictions from different classes.
        eligible = sorted((defect for defect in predictions[sample_id]
            if defect["score"] >= config["modelEvaluationScoreFloor"]),
            key=lambda defect: -defect["score"])[:max_detections]
        for defect in eligible:
            detections.append({"image_id": image_id, "category_id": defect["classId"],
                "bbox": _bbox(defect), "score": defect["score"]})

    categories = [{"id": index, "name": name} for index, name in enumerate(CLASS_NAMES, start=1)]
    # COCO's console summaries are not part of this pure function's report.
    with redirect_stdout(StringIO()):
        ground_truth = COCO()
        ground_truth.dataset = {"images": images, "annotations": annotations, "categories": categories}
        ground_truth.createIndex()
        if detections:
            result = ground_truth.loadRes(detections)
        else:
            # loadRes indexes its first annotation; an empty result needs an explicit COCO object.
            result = COCO()
            result.dataset = {"images": images, "annotations": [], "categories": categories}
            result.createIndex()
        evaluator = COCOeval(ground_truth, result, "bbox")
        evaluator.params.imgIds = [image["id"] for image in images]
        evaluator.params.catIds = list(range(1, len(CLASS_NAMES) + 1))
        evaluator.params.iouThrs = thresholds
        evaluator.params.recThrs = np.linspace(0, 1, 101)
        evaluator.params.maxDets = [1, 10, max_detections]
        evaluator.evaluate()
        evaluator.accumulate()

    # COCO precision dimensions are IoU, recall, category, area, maximum detections.
    precision = evaluator.eval["precision"][:, :, :, 0, -1]
    return {"map50": _mean_ap(precision[0]), "map50To95": _mean_ap(precision),
        "classes": [{"ap50": _mean_ap(precision[0, :, index]),
            "ap50To95": _mean_ap(precision[:, :, index])} for index in range(len(CLASS_NAMES))]}


def evaluate_predictions(truth_rows, prediction_rows, evaluation_config):
    """Return deployment counts, geometric confusion and COCO AP without reading files.

    Every truth must have exactly one Completed or Failed prediction row. Failed rows
    contribute no predictions, even when a caller retained partial detections. AP is
    null for classes without truth; COCO excludes those classes from the macro mean.
    Latency is intentionally summarized by the caller, which knows its warmup/cold
    startup boundaries and the stages included in each elapsedMs measurement.
    """
    truths = _index_rows(truth_rows, "truth")
    rows = _index_rows(prediction_rows, "prediction")
    if truths.keys() != rows.keys():
        missing = sorted(truths.keys() - rows.keys())
        unexpected = sorted(rows.keys() - truths.keys())
        raise ValueError(f"Prediction population mismatch; missing={missing}, unexpected={unexpected}")
    if not truths:
        raise ValueError("Evaluation population must contain at least one sample")

    counts = [[0, 0, 0] for _ in CLASS_NAMES]
    confusion = [[0] * (len(CLASS_NAMES) + 1) for _ in range(len(CLASS_NAMES) + 1)]
    failed_samples, predictions = [], {}
    threshold = evaluation_config["iouThreshold"]
    score_thresholds = evaluation_config["scoreThresholds"]
    for sample_id, row in truths.items():
        prediction = rows[sample_id]
        if prediction["execution"] not in ("Completed", "Failed"):
            raise ValueError(f"Unknown execution state for {sample_id}: {prediction['execution']}")
        if prediction["execution"] == "Failed":
            failed_samples.append(sample_id)
            defects = []
        else:
            defects = prediction["defects"]
        predictions[sample_id] = defects
        # AP consumes the raw candidates above. Only deployment PR/confusion use this selection.
        defects = deployment_predictions(defects, score_thresholds)
        truth_defects = row["defects"]
        matched = match(defects, truth_defects, threshold, confidence=0, class_aware=True)
        for _, truth_index in matched["matches"]:
            counts[truth_defects[truth_index]["classId"] - 1][0] += 1
        for prediction_index in matched["falsePositives"]:
            counts[defects[prediction_index]["classId"] - 1][1] += 1
        for truth_index in matched["falseNegatives"]:
            counts[truth_defects[truth_index]["classId"] - 1][2] += 1

        geometry = match(defects, truth_defects, threshold, confidence=0, class_aware=False)
        for prediction_index, truth_index in geometry["matches"]:
            confusion[truth_defects[truth_index]["classId"]][defects[prediction_index]["classId"]] += 1
        for prediction_index in geometry["falsePositives"]:
            confusion[0][defects[prediction_index]["classId"]] += 1
        for truth_index in geometry["falseNegatives"]:
            confusion[truth_defects[truth_index]["classId"]][0] += 1

    ap = _coco_ap(truths, predictions, evaluation_config)
    totals = [sum(class_counts[index] for class_counts in counts) for index in range(3)]
    return {"images": len(truths), "groundTruthDefects": totals[0] + totals[2],
        "executionCompleted": len(truths) - len(failed_samples),
        "executionFailures": len(failed_samples), "failedSamples": failed_samples,
        "operatingPoint": operating_point(*totals),
        "classes": {name: {"classId": index + 1, **operating_point(*counts[index]),
            **ap["classes"][index]} for index, name in enumerate(CLASS_NAMES)},
        "map50": ap["map50"], "map50To95": ap["map50To95"],
        "confusionMatrix": {"labels": ["background", *CLASS_NAMES],
            "rowAxis": "truth", "columnAxis": "prediction", "matrix": confusion},
        "protocol": {"version": evaluation_config["protocolVersion"],
            "operatingPointMatching": "class-aware, descending score, one-to-one, largest IoU",
            "iouThreshold": threshold, "scoreThresholds": list(score_thresholds),
            "confusionMatching": "class-agnostic geometry at the operating point",
            "apImplementation": "pycocotools.COCOeval bbox",
            "mapIoUThresholds": list(evaluation_config["mapIoUThresholds"]),
            "recallPoints": 101, "maxDetectionsPerImage": evaluation_config["maxDetectionsPerImage"],
            "modelEvaluationScoreFloor": evaluation_config["modelEvaluationScoreFloor"],
            "apAveraging": "macro across classes with ground truth; absent class AP is null"}}
