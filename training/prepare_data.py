"""Pin official DeepPCB splits; keep deployment input manifests free of ground truth."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import subprocess

from PIL import Image, ImageDraw

REVISION = "08e98c4db5922613fb97176eb3d6497d48260cb1"
VALIDATION_GROUPS = {"group44000", "group50600", "group77000"}
CLASSES = {1: "open", 2: "short", 3: "mousebite", 4: "spur", 5: "copper", 6: "pin-hole"}


def write_jsonl(path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")


def prepare(root, output, audit_dir):
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if revision != REVISION:
        raise ValueError(f"Expected DeepPCB {REVISION}, found {revision}")
    database = root / "PCBData"
    inputs, truth = defaultdict(list), defaultdict(list)
    hashes = defaultdict(list)
    for official in ("trainval", "test"):
        for line in (database / f"{official}.txt").read_text().splitlines():
            virtual, annotation = line.split()
            path = Path(virtual)
            sample_id, group = path.stem, path.parts[0]
            split = "test" if official == "test" else ("validation" if group in VALIDATION_GROUPS else "train")
            assets = {"image": path.with_name(sample_id + "_test.jpg"), "reference": path.with_name(sample_id + "_temp.jpg")}
            item = {"sampleId": sample_id, "sourceGroup": group, "officialSplit": official}
            for kind, asset in assets.items():
                full = database / asset
                with Image.open(full) as image:
                    image.load()
                    if image.size != (640, 640):
                        raise ValueError(f"Unexpected dimensions: {full} {image.size}")
                    pixel_hash = hashlib.sha256(image.convert("L").tobytes()).hexdigest()
                item[kind] = "DeepPCB/PCBData/" + asset.as_posix()
                item[kind + "Sha256"] = hashlib.sha256(full.read_bytes()).hexdigest()
                hashes[kind + ":" + pixel_hash].append([split, sample_id])
            defects = []
            for label in (database / annotation).read_text().splitlines():
                x1, y1, x2, y2, class_id = map(int, label.split())
                if not (0 <= x1 < x2 <= 640 and 0 <= y1 < y2 <= 640 and class_id in CLASSES):
                    raise ValueError(f"Invalid annotation: {annotation}: {label}")
                defects.append({"box": [x1, y1, x2, y2], "classId": class_id})
            inputs[split].append(item)
            truth[split].append({"sampleId": sample_id, "defects": defects})
    audit_dir.mkdir(parents=True, exist_ok=True)
    report = {"revision": revision, "validationGroups": sorted(VALIDATION_GROUPS), "classes": CLASSES, "splits": {}}
    for split, rows in inputs.items():
        rows.sort(key=lambda row: row["sampleId"])
        truth[split].sort(key=lambda row: row["sampleId"])
        write_jsonl(output / "inputs" / f"{split}.jsonl", rows)
        write_jsonl(output / "truth" / f"{split}.jsonl", truth[split])
        report["splits"][split] = {"images": len(rows), "groups": dict(Counter(row["sourceGroup"] for row in rows)),
            "defects": dict(sorted(Counter(d["classId"] for row in truth[split] for d in row["defects"]).items()))}
    report["crossSplitExactPixelDuplicates"] = {key: rows for key, rows in hashes.items() if len({r[0] for r in rows}) > 1}
    report["testGroupOverlap"] = sorted({r["sourceGroup"] for r in inputs["test"]} & {r["sourceGroup"] for s in ("train", "validation") for r in inputs[s]})
    (output / "dataset-audit.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    canvas = Image.new("RGB", (1280, 640))
    for index, split in enumerate(("train", "validation")):
        item = inputs[split][0]
        tile = Image.open(root.parent / item["image"]).convert("RGB")
        draw = ImageDraw.Draw(tile)
        for defect in truth[split][0]["defects"]:
            draw.rectangle(defect["box"], outline="red", width=2)
            draw.text(tuple(defect["box"][:2]), CLASSES[defect["classId"]], fill="yellow")
        canvas.paste(tile, (index * 640, 0))
    canvas.save(audit_dir / "development-samples.png")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("data/DeepPCB"))
    parser.add_argument("--output", type=Path, default=Path("training/manifests"))
    parser.add_argument("--audit-dir", type=Path, default=Path("artifacts/data"))
    args = parser.parse_args()
    prepare(args.root, args.output, args.audit_dir)
