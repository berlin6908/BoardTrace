"""Fixed one-to-one deployment operating-point metrics; COCO AP uses pycocotools."""
def iou(left, right):
    width = max(0, min(left[2], right[2]) - max(left[0], right[0]))
    height = max(0, min(left[3], right[3]) - max(left[1], right[1]))
    intersection = width * height
    union = (left[2] - left[0]) * (left[3] - left[1]) + (right[2] - right[0]) * (right[3] - right[1]) - intersection
    return intersection / union if union else 0


def match(predictions, truths, threshold=0.5, confidence=0.5, class_aware=True):
    unmatched = set(range(len(truths)))
    pairs, false_positives = [], []
    order = sorted(range(len(predictions)), key=lambda index: (-predictions[index]["score"], index))
    for index in order:
        prediction = predictions[index]
        if prediction["score"] < confidence:
            continue
        candidates = [i for i in sorted(unmatched) if not class_aware or truths[i]["classId"] == prediction["classId"]]
        eligible = [(iou(prediction["box"], truths[i]["box"]), i) for i in candidates]
        eligible = [(overlap, i) for overlap, i in eligible if overlap >= threshold]
        if eligible:
            _, best = max(eligible, key=lambda item: (item[0], -item[1]))
            unmatched.remove(best)
            pairs.append((index, best))
        else:
            false_positives.append(index)
    return {"matches": pairs, "falsePositives": false_positives, "falseNegatives": sorted(unmatched)}


def operating_point(true_positives, false_positives, false_negatives):
    precision = true_positives / (true_positives + false_positives) if true_positives + false_positives else 0
    recall = true_positives / (true_positives + false_negatives) if true_positives + false_negatives else 0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0
    return {"tp": true_positives, "fp": false_positives, "fn": false_negatives,
            "precision": precision, "recall": recall, "f1": f1}
