"""Development data only; transforms apply jointly to images and bounding boxes."""
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np
import torch
from torch.utils.data import Dataset
from torchvision import tv_tensors
from torchvision.transforms import v2


class DeepPcbDataset(Dataset):
    def __init__(self, data_root, manifest_root, split, horizontal_flip=0.0, vertical_flip=0.0, limit=None):
        if split not in ("train", "validation"):
            raise ValueError("Training and model selection accept only train/validation splits")
        self.data_root = Path(data_root)
        self.input_path = Path(manifest_root) / "inputs" / f"{split}.jsonl"
        self.truth_path = Path(manifest_root) / "truth" / f"{split}.jsonl"
        rows = [json.loads(line) for line in self.input_path.read_text(encoding="utf-8").splitlines()]
        truths = [json.loads(line) for line in self.truth_path.read_text(encoding="utf-8").splitlines()]
        truth_by_id = {row["sampleId"]: row for row in truths}
        if len(truths) != len(truth_by_id) or len({row["sampleId"] for row in rows}) != len(rows):
            raise ValueError("Duplicate input or truth sample IDs")
        if {row["sampleId"] for row in rows} != truth_by_id.keys():
            raise ValueError("Input and truth populations differ")
        self.rows = rows if limit is None else rows[:limit]
        self.truths = [truth_by_id[row["sampleId"]] for row in self.rows]
        if not self.rows:
            raise ValueError("Empty dataset")
        self.transform = v2.Compose([v2.RandomHorizontalFlip(horizontal_flip), v2.RandomVerticalFlip(vertical_flip), v2.ToPureTensor()])

    def __len__(self):
        return len(self.rows)

    def load_image(self, index):
        channels = []
        for path_key, hash_key in (("image", "imageSha256"), ("reference", "referenceSha256")):
            raw = (self.data_root / self.rows[index][path_key]).read_bytes()
            if hashlib.sha256(raw).hexdigest() != self.rows[index][hash_key]:
                raise ValueError(f"{path_key} differs from the pinned manifest: {self.rows[index]['sampleId']}")
            image = cv2.imdecode(np.frombuffer(raw, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
            if image is None or image.shape != (640, 640):
                raise ValueError(f"Invalid 640x640 {path_key}: {self.rows[index]['sampleId']}")
            channels.append(image)
        tested, reference = channels
        paired = np.stack((tested, reference, cv2.absdiff(tested, reference)))
        return torch.from_numpy(paired.copy()).float().div_(255)

    def __getitem__(self, index):
        image = tv_tensors.Image(self.load_image(index))
        defects = self.truths[index]["defects"]
        boxes = torch.tensor([item["box"] for item in defects], dtype=torch.float32).reshape(-1, 4)
        target = {"boxes": tv_tensors.BoundingBoxes(boxes, format="XYXY", canvas_size=(640, 640)),
                  "labels": torch.tensor([item["classId"] for item in defects], dtype=torch.int64)}
        return self.transform(image, target)

    def identity(self):
        return {"images": len(self), "sampleIds": [row["sampleId"] for row in self.rows],
                "inputsSha256": hashlib.sha256(self.input_path.read_bytes()).hexdigest(),
                "truthSha256": hashlib.sha256(self.truth_path.read_bytes()).hexdigest()}


def collate_batch(batch):
    return tuple(zip(*batch))
