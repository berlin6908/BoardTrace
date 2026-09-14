"""Score detector-emitted deployment results without applying another score filter."""
import argparse
import json
from pathlib import Path

import numpy as np

from metrics import match, operating_point


def score(truth_path, predictions_path, output_path, class_aware):
    truths = [json.loads(line) for line in truth_path.read_text(encoding="utf-8").splitlines()]
    rows = [json.loads(line) for line in predictions_path.read_text(encoding="utf-8").splitlines()]
    predictions = {row["sampleId"]: row for row in rows}
    if len(rows) != len(predictions):
        raise ValueError("Duplicate prediction sample IDs")
    if predictions.keys() != {row["sampleId"] for row in truths}:
        raise ValueError("Prediction population is incomplete or includes unexpected samples; evaluation run did not finish")
    tp = fp = fn = failures = 0
    elapsed = []
    failed_samples = []
    for row in truths:
        prediction = predictions[row["sampleId"]]
        if prediction["execution"] != "Completed":
            failures += 1
            failed_samples.append(row["sampleId"])
            defects = []
        else:
            defects = prediction["defects"]
            elapsed.append(prediction["elapsedMs"])
        # The detector already applied its configured per-class operating thresholds.
        matched = match(defects, row["defects"], confidence=0, class_aware=class_aware)
        tp += len(matched["matches"])
        fp += len(matched["falsePositives"])
        fn += len(matched["falseNegatives"])
    report = {"images": len(truths), "executionFailures": failures, "failedSamples": failed_samples,
        "groundTruthDefects": tp + fn, "classAware": class_aware, "iou": 0.5, "additionalScoreFiltering": False,
        **operating_point(tp, fp, fn), "latencyIncludesColdStart": True,
        "latencySamples": len(elapsed), "detectionP50Ms": float(np.percentile(elapsed, 50)) if elapsed else None,
        "detectionP95Ms": float(np.percentile(elapsed, 95)) if elapsed else None}
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--truth", required=True, type=Path)
    parser.add_argument("--predictions", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--class-aware", action="store_true")
    args = parser.parse_args()
    score(args.truth, args.predictions, args.output, args.class_aware)
