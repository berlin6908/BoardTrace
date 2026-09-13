"""Train six defect classes and select checkpoints on development validation only."""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import random
import subprocess
import time

import cv2
import numpy as np
import torch
from torch.utils.data import DataLoader
import torchvision

from dataset import DeepPcbDataset, collate_batch
from detection_evaluation import evaluate_predictions
from model import build_model


def write_json(path, content):
    temporary = path.with_suffix(path.suffix + ".partial")
    temporary.write_text(json.dumps(content, indent=2, allow_nan=False), encoding="utf-8")
    temporary.replace(path)


def save_checkpoint(path, content):
    temporary = path.with_suffix(".partial")
    torch.save(content, temporary)
    temporary.replace(path)


def set_seed(seed):
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False


def seed_worker(_):
    worker_seed = torch.initial_seed() % 2**32
    np.random.seed(worker_seed)
    random.seed(worker_seed)
    cv2.setNumThreads(1)


def capture_rng(generator):
    return {"python": random.getstate(), "numpy": np.random.get_state(), "torch": torch.get_rng_state(),
            "cuda": torch.cuda.get_rng_state_all() if torch.cuda.is_available() else [],
            "loader": generator.get_state()}


def restore_rng(state, generator):
    random.setstate(state["python"])
    np.random.set_state(state["numpy"])
    torch.set_rng_state(state["torch"])
    if state["cuda"]:
        torch.cuda.set_rng_state_all(state["cuda"])
    generator.set_state(state["loader"])


def synchronize(device):
    if device.type == "cuda":
        torch.cuda.synchronize(device)


@torch.inference_mode()
def validate(model, dataset, device, protocol):
    model.eval()
    rows = []
    for index, item in enumerate(dataset.rows):
        started = time.perf_counter()
        row = {"sampleId": item["sampleId"], "execution": "Completed", "defects": []}
        try:
            synchronize(device)
            # Validation inference receives only image pixels; truth is used afterwards by the evaluator.
            image = dataset.load_image(index).to(device)
            result = model([image])[0]
            boxes, labels, scores = (result[key].cpu().tolist() for key in ("boxes", "labels", "scores"))
            row["defects"] = [{"box": box, "classId": label, "score": score}
                              for box, label, score in zip(boxes, labels, scores)
                              if score >= protocol["modelEvaluationScoreFloor"]]
            synchronize(device)
        except (OSError, ValueError, RuntimeError, cv2.error) as error:
            row["execution"] = "Failed"
            row["defects"] = []
            row["error"] = f"{type(error).__name__}: {error}"
        row["elapsedMs"] = (time.perf_counter() - started) * 1000
        rows.append(row)
    report = evaluate_predictions(dataset.truths, rows, protocol)
    warm = [row["elapsedMs"] for row in rows[1:] if row["execution"] == "Completed"]
    report["latency"] = {"scope": "decode+RGB float CHW+device transfer+model+CPU outputs", "concurrency": 1,
                         "excludedInitialCalls": 1, "initialCallMs": rows[0]["elapsedMs"],
                         "initialCallExecution": rows[0]["execution"], "steadySamples": len(warm),
                         "p50Ms": float(np.percentile(warm, 50)) if warm else None,
                         "p95Ms": float(np.percentile(warm, 95)) if warm else None}
    return report, rows


def train_epoch(model, loader, optimizer, device, epoch):
    model.train()
    totals = defaultdict(float)
    samples = 0
    synchronize(device)
    started = time.perf_counter()
    for step, (images, targets) in enumerate(loader, 1):
        images = [image.to(device) for image in images]
        targets = [{key: value.to(device) for key, value in target.items()} for target in targets]
        losses = model(images, targets)
        loss = sum(losses.values())
        if not torch.isfinite(loss):
            raise RuntimeError(f"Non-finite training loss at epoch {epoch}, step {step}")
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        for name, value in {**losses, "total": loss}.items():
            totals[name] += float(value.detach()) * len(images)
        samples += len(images)
        if step == 1 or step % 25 == 0 or step == len(loader):
            print(json.dumps({"kind": "trainingStep", "epoch": epoch, "step": step, "samples": samples,
                              "loss": float(loss.detach()), "learningRate": optimizer.param_groups[0]["lr"]}), flush=True)
    synchronize(device)
    return {"samples": samples, "steps": len(loader), "seconds": time.perf_counter() - started,
            "losses": {key: value / samples for key, value in totals.items()}}


def run(args):
    config = json.loads(args.config.read_text(encoding="utf-8"))
    protocol = json.loads(args.evaluation_config.read_text(encoding="utf-8"))
    if config["epochs"] < 1 or config["batchSize"] < 1 or config["workers"] < 0:
        raise ValueError("epochs/batchSize must be positive and workers nonnegative")
    if args.smoke_samples is not None and args.smoke_samples < 1:
        raise ValueError("Smoke sample count must be positive")
    device = torch.device(args.device)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA requested but unavailable")
    torch.set_num_threads(config["threads"])
    cv2.setNumThreads(1)
    set_seed(config["seed"])
    training = DeepPcbDataset(args.data_root, args.manifests, "train",
        config["horizontalFlipProbability"], config["verticalFlipProbability"], args.smoke_samples)
    validation = DeepPcbDataset(args.data_root, args.manifests, "validation", limit=args.smoke_samples)
    sources = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
               for path in [Path(__file__), Path(__file__).with_name("model.py"),
                            Path(__file__).with_name("dataset.py"), Path(__file__).with_name("detection_evaluation.py"),
                            Path(__file__).with_name("metrics.py")]}
    identity = {"config": config, "evaluationProtocol": protocol, "data": {"train": training.identity(), "validation": validation.identity()},
                "sourceSha256": sources, "kind": "training-smoke-not-quality-evaluation" if args.smoke_samples else "development-training"}
    args.output.mkdir(parents=True, exist_ok=True)
    checkpoint_path = args.output / "last.pt"
    if not args.resume and any(args.output.iterdir()):
        raise ValueError("Output directory is nonempty; use a new run directory or --resume")
    generator = torch.Generator().manual_seed(config["seed"])
    loader = DataLoader(training, batch_size=config["batchSize"], shuffle=True,
        num_workers=config["workers"], collate_fn=collate_batch, generator=generator, worker_init_fn=seed_worker,
        persistent_workers=False, pin_memory=device.type == "cuda")
    model = build_model(pretrained=not args.resume).to(device)
    optimizer = torch.optim.SGD([p for p in model.parameters() if p.requires_grad],
        lr=config["learningRate"], momentum=config["momentum"], weight_decay=config["weightDecay"])
    scheduler = torch.optim.lr_scheduler.MultiStepLR(optimizer, milestones=config["lrMilestones"], gamma=config["lrGamma"])
    start_epoch, best_score, best_epoch, history = 1, -1.0, None, []
    if args.resume:
        # This is a locally produced training checkpoint, including Python/NumPy RNG states.
        state = torch.load(checkpoint_path, map_location="cpu", weights_only=False)
        if state["identity"] != identity:
            raise ValueError("Resume requires the same code, configuration and development data")
        model.load_state_dict(state["model"])
        optimizer.load_state_dict(state["optimizer"])
        scheduler.load_state_dict(state["scheduler"])
        restore_rng(state["rng"], generator)
        start_epoch, best_score, best_epoch, history = state["epoch"] + 1, state["bestScore"], state["bestEpoch"], state["history"]
    hardware = {"startedUtc": datetime.now(timezone.utc).isoformat(), "host": platform.node(), "platform": platform.platform(),
        "python": platform.python_version(), "torch": torch.__version__, "torchvision": torchvision.__version__,
        "pycocotools": importlib.metadata.version("pycocotools"), "device": str(device), "slurmJobId": os.getenv("SLURM_JOB_ID"),
        "gpu": torch.cuda.get_device_name(device) if device.type == "cuda" else None,
        "gpuMemoryBytes": torch.cuda.get_device_properties(device).total_memory if device.type == "cuda" else None,
        "cudaRuntime": torch.version.cuda, "cudnn": torch.backends.cudnn.version(),
        "cudnnDeterministic": True, "cudnnBenchmark": False, "tf32": False,
        "reproducibility": "Saved Python/NumPy/Torch/CUDA/loader RNG and optimizer/scheduler state; cross-hardware bitwise identity is not assumed",
        "gitCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "resumedFromEpoch": start_epoch - 1}
    write_json(args.output / f"invocation-{start_epoch:03d}.json", {**identity, "hardware": hardware})
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats(device)
    end_epoch = min(config["epochs"], args.stop_after_epoch or config["epochs"])
    for epoch in range(start_epoch, end_epoch + 1):
        learning_rate = optimizer.param_groups[0]["lr"]
        trained = train_epoch(model, loader, optimizer, device, epoch)
        report, predictions = validate(model, validation, device, protocol)
        score = report["map50To95"]
        eligible = report["executionFailures"] == 0
        selected = eligible and score > best_score
        if selected:
            best_score, best_epoch = score, epoch
        scheduler.step()
        row = {"epoch": epoch, "learningRate": learning_rate, "training": trained, "validation": report,
               "eligibleForSelection": eligible, "selectedBest": selected,
               "peakCudaMemoryBytes": torch.cuda.max_memory_allocated(device) if device.type == "cuda" else None}
        history.append(row)
        state = {"identity": identity, "epoch": epoch, "model": model.state_dict(), "optimizer": optimizer.state_dict(),
                 "scheduler": scheduler.state_dict(), "rng": capture_rng(generator),
                 "bestScore": best_score, "bestEpoch": best_epoch, "history": history}
        if selected:
            save_checkpoint(args.output / "best.pt", {"identity": identity, "epoch": epoch, "model": model.state_dict(), "validation": report})
        save_checkpoint(checkpoint_path, state)
        write_json(args.output / "history.json", history)
        write_json(args.output / f"validation-{epoch:03d}.json", report)
        write_json(args.output / f"validation-{epoch:03d}-predictions.json", predictions)
        print(json.dumps({"kind": "epoch", "epoch": epoch, "loss": trained["losses"]["total"], "map50": report["map50"],
                          "map50To95": score, "executionFailures": report["executionFailures"], "selectedBest": selected}), flush=True)
        if not eligible:
            raise RuntimeError("Validation had execution failures; all inputs retained in report; checkpoint not eligible as best")
    write_json(args.output / "summary.json", {"kind": identity["kind"], "completedEpochs": history[-1]["epoch"] if history else 0,
        "plannedEpochs": config["epochs"], "bestEpoch": best_epoch, "bestMap50To95": best_score if best_epoch else None,
        "selectionMetric": "validation COCO bbox mAP50:95; earlier epoch wins ties", "testSetUsed": False})


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("training/train-config.json"))
    parser.add_argument("--evaluation-config", type=Path, default=Path("training/evaluation-config.json"))
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--manifests", type=Path, default=Path("training/manifests"))
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cuda")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--smoke-samples", type=int, help="Limit both development splits and explicitly mark run as smoke")
    parser.add_argument("--stop-after-epoch", type=int, help="Stop at an epoch boundary while retaining the planned schedule")
    return parser.parse_args()


if __name__ == "__main__":
    run(parse_args())
