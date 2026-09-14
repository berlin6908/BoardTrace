"""Evaluate frozen paired ONNX bytes; inference never receives the truth records."""
import argparse
from datetime import datetime, timezone
import hashlib
from importlib.metadata import version
import json
import os
from pathlib import Path
import platform
import time

import cv2
import numpy as np
import onnxruntime as ort

from detection_evaluation import deployment_predictions, evaluate_predictions


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")


def read_jsonl(raw):
    return [json.loads(line) for line in raw.decode("utf-8-sig").splitlines() if line.strip()]


def validate_protocol(protocol):
    if "scoreThresholds" not in protocol or "confidenceThreshold" in protocol:
        raise ValueError("The protocol requires six scoreThresholds; scalar confidenceThreshold is not supported")
    deployment_predictions([], protocol["scoreThresholds"])
    if (protocol["protocolVersion"] != "2" or protocol["iouThreshold"] != 0.5
            or protocol["modelEvaluationScoreFloor"] != 0.001 or protocol["maxDetectionsPerImage"] != 100):
        raise ValueError("Expected protocol v2, IoU 0.5, raw score floor 0.001 and maximum 100 detections")
    thresholds = np.asarray(protocol["mapIoUThresholds"])
    if thresholds.shape != (10,) or not np.allclose(thresholds, np.linspace(0.5, 0.95, 10)):
        raise ValueError("Expected mAP IoU thresholds 0.50 through 0.95")


def validate_model(session):
    inputs = session.get_inputs()
    if (len(inputs) != 1 or inputs[0].name != "images" or inputs[0].type != "tensor(float)"
            or inputs[0].shape != [1, 3, 640, 640]):
        raise ValueError("ONNX input must be images: float32 [1,3,640,640]")
    outputs = {output.name: output for output in session.get_outputs()}
    for name, kind, rank in (("boxes", "tensor(float)", 2), ("labels", "tensor(int64)", 1),
            ("scores", "tensor(float)", 1)):
        output = outputs.get(name)
        if (output is None or output.type != kind or len(output.shape) != rank
                or (name == "boxes" and output.shape[1] != 4)):
            raise ValueError("ONNX outputs must be boxes: float32 Nx4, labels: int64 N and scores: float32 N")


def paired_input(tested_bytes, reference_bytes):
    planes = []
    for name, raw in (("tested", tested_bytes), ("reference", reference_bytes)):
        if not raw:
            raise ValueError(f"Empty {name} image")
        image = cv2.imdecode(np.frombuffer(raw, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if image is None or image.shape != (640, 640):
            raise ValueError(f"Invalid 640x640 {name} image")
        if cv2.meanStdDev(image)[1][0, 0] < 5:
            raise ValueError(f"Insufficient contrast in {name} image")
        planes.append(image)
    # The graph owns mean/std 0.5 and NMS. absdiff avoids unsigned subtraction wraparound.
    tested, reference = planes
    return np.stack((tested, reference, cv2.absdiff(tested, reference)))[None].astype(np.float32) / np.float32(255)


def raw_defects(outputs):
    boxes, labels, scores = outputs
    count = scores.size
    if (scores.shape != (count,) or labels.shape != (count,) or boxes.shape != (count, 4)
            or count > 100 or boxes.dtype != np.float32 or labels.dtype != np.int64 or scores.dtype != np.float32):
        raise ValueError("Invalid ONNX candidate shapes, types or maximum count")
    if (not np.all(np.isfinite(boxes)) or not np.all(np.isfinite(scores))
            or np.any((scores < 0) | (scores > 1)) or np.any((labels < 1) | (labels > 6))
            or np.any(boxes < 0) or np.any(boxes > 640)
            or np.any(boxes[:, 2:] < boxes[:, :2])):
        raise ValueError("Invalid ONNX class, score or xyxy box")
    # Preserve graph order and every raw candidate; the existing scorer applies its AP floor.
    return [{"box": box.tolist(), "classId": int(label), "score": float(score)}
        for box, label, score in zip(boxes, labels, scores)]


def infer_sample(session, sample, data_root):
    started = time.perf_counter()
    row = {"sampleId": sample["sampleId"], "execution": "Failed", "defects": [],
        "imageSha256": None, "referenceSha256": None}
    detect_started = None
    try:
        images = []
        for path_key, hash_key in (("image", "imageSha256"), ("reference", "referenceSha256")):
            raw = (data_root / sample[path_key]).read_bytes()
            row[hash_key] = hashlib.sha256(raw).hexdigest()
            if row[hash_key] != sample[hash_key]:
                raise ValueError(f"{path_key} SHA-256 differs from the frozen input manifest")
            images.append(raw)
        row["inputReadAndHashMs"] = (time.perf_counter() - started) * 1000
        detect_started = time.perf_counter()
        tensor = paired_input(*images)
        defects = raw_defects(session.run(["boxes", "labels", "scores"], {"images": tensor}))
        row.update(execution="Completed", defects=defects)
    except Exception as error:
        # A batch retains every accepted input, including decode/ORT failures, for the full denominator.
        row.update(error=str(error), errorType=type(error).__name__)
    row["elapsedMs"] = (time.perf_counter() - (detect_started or started)) * 1000
    row["attemptMs"] = (time.perf_counter() - started) * 1000
    return row


def timing_summary(rows):
    warm = [row["elapsedMs"] for row in rows[1:] if row["execution"] == "Completed"]
    return {"coldDetectMs": rows[0]["elapsedMs"] if rows[0]["execution"] == "Completed" else None,
        "warmSamples": len(warm), "warmP50Ms": float(np.percentile(warm, 50)) if warm else None,
        "warmP95Ms": float(np.percentile(warm, 95)) if warm else None,
        "scope": "raw candidate collection on this CPU; not Windows station latency acceptance",
        "elapsedMsIncludes": "decode, dimensions/contrast checks, paired preprocessing, ORT Run, output validation/extraction",
        "elapsedMsExcludes": "file read/hash and session initialization; failures before decode use full attempt time"}


def run_evaluation(args):
    started = time.perf_counter()
    args.output.mkdir(parents=True, exist_ok=False)
    run = {"kind": "frozen-paired-onnx-evaluation", "state": "Started",
        "startedAt": datetime.now(timezone.utc).isoformat(), "arguments": {
            key: str(value) for key, value in vars(args).items()}}
    try:
        model_bytes = args.model.read_bytes()
        model_sha = hashlib.sha256(model_bytes).hexdigest()
        if model_sha != args.model_sha256.lower():
            raise ValueError("Model SHA-256 differs from the explicitly selected frozen model")
        input_bytes = args.inputs.read_bytes()
        samples = read_jsonl(input_bytes)
        ids = [sample["sampleId"] for sample in samples]
        if not ids or any(not isinstance(value, str) or not value for value in ids) or len(ids) != len(set(ids)):
            raise ValueError("Input population must contain unique nonempty sample IDs")
        protocol_bytes = args.evaluation_config.read_bytes()
        protocol = json.loads(protocol_bytes)
        validate_protocol(protocol)
        run.update(modelSha256=model_sha, modelByteLength=len(model_bytes),
            inputContract="PairedGrayAbsDiff640V1", inputsSha256=hashlib.sha256(input_bytes).hexdigest(),
            evaluationConfigSha256=hashlib.sha256(protocol_bytes).hexdigest(), evaluationConfig=protocol,
            inputSamples=len(samples), sourceSha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                for name in ("evaluate_onnx.py", "detection_evaluation.py", "metrics.py")})
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        session_started = time.perf_counter()
        session = ort.InferenceSession(model_bytes, sess_options=options, providers=["CPUExecutionProvider"])
        run["sessionInitializationMs"] = (time.perf_counter() - session_started) * 1000
        del model_bytes
        validate_model(session)
        run["environment"] = {"python": platform.python_version(), "platform": platform.platform(),
            "machine": platform.machine(), "processor": platform.processor(), "host": platform.node(),
            "logicalCpus": os.cpu_count(), "numpy": np.__version__, "opencv": cv2.__version__,
            "onnxruntime": ort.__version__, "pycocotools": version("pycocotools"),
            "availableProviders": ort.get_available_providers(), "sessionProviders": session.get_providers(),
            "ortIntraOpThreads": 4, "opencvThreads": cv2.getNumThreads(),
            "slurmJobId": os.getenv("SLURM_JOB_ID"), "slurmCpusPerTask": os.getenv("SLURM_CPUS_PER_TASK")}
        write_json(args.output / "run.json", run)
        rows = []
        partial = args.output / "raw-predictions.jsonl.partial"
        with partial.open("w", encoding="utf-8") as output:
            for sample in samples:
                row = infer_sample(session, sample, args.data_root)
                rows.append(row)
                output.write(json.dumps(row, allow_nan=False) + "\n")
                output.flush()
                if len(rows) % 25 == 0 or len(rows) == len(samples):
                    print(json.dumps({"processed": len(rows), "total": len(samples),
                        "failures": sum(row["execution"] == "Failed" for row in rows)}), flush=True)
        partial.replace(args.output / "raw-predictions.jsonl")
        del session
        run["timing"] = timing_summary(rows)
        # Truth is first opened after the complete input-only inference pass has been persisted.
        truth_bytes = args.truth.read_bytes()
        run["truthSha256"] = hashlib.sha256(truth_bytes).hexdigest()
        report = evaluate_predictions(read_jsonl(truth_bytes), rows, protocol)
        write_json(args.output / "report.json", report)
        failures = report["executionFailures"]
        run.update(state="CompletedWithFailures" if failures else "Completed",
            completed=report["executionCompleted"], failures=failures)
        return 1 if failures else 0
    except Exception as error:
        run.update(state="Failed", error=str(error), errorType=type(error).__name__)
        raise
    finally:
        run.update(endedAt=datetime.now(timezone.utc).isoformat(), totalElapsedMs=(time.perf_counter() - started) * 1000)
        write_json(args.output / "run.json", run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--model-sha256", required=True)
    parser.add_argument("--inputs", required=True, type=Path)
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--truth", required=True, type=Path)
    parser.add_argument("--evaluation-config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="New directory; existing runs are never overwritten")
    raise SystemExit(run_evaluation(parser.parse_args()))
