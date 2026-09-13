import sys
import json
import hashlib
from pathlib import Path
import random
from types import SimpleNamespace

import cv2
import numpy as np
import pytest
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "training"))
from model import build_model
from dataset import DeepPcbDataset
from train import capture_rng, restore_rng, set_seed
import train


def test_pretrained_training_optimizer_can_resume_without_downloading_weights():
    original = build_model(pretrained=True)
    optimizer = torch.optim.SGD([p for p in original.parameters() if p.requires_grad], lr=0.002, momentum=0.9)
    restored = build_model(pretrained=False)
    restored.load_state_dict(original.state_dict())
    restored_optimizer = torch.optim.SGD([p for p in restored.parameters() if p.requires_grad], lr=0.002, momentum=0.9)
    restored_optimizer.load_state_dict(optimizer.state_dict())


def test_joint_flips_keep_training_box_on_the_image_defect(tmp_path):
    image = np.zeros((640, 640, 3), dtype=np.uint8)
    image[20:40, 10:30] = 255
    cv2.imwrite(str(tmp_path / "image.png"), image)
    for directory in ("inputs", "truth"):
        (tmp_path / directory).mkdir()
    (tmp_path / "inputs/train.jsonl").write_text(json.dumps({"sampleId": "example", "image": "image.png",
        "imageSha256": hashlib.sha256((tmp_path / "image.png").read_bytes()).hexdigest()}), encoding="utf-8")
    (tmp_path / "truth/train.jsonl").write_text(json.dumps({"sampleId": "example", "defects": [{"box": [10, 20, 30, 40], "classId": 1}]}), encoding="utf-8")
    dataset = DeepPcbDataset(tmp_path, tmp_path, "train", horizontal_flip=1.0, vertical_flip=1.0)
    transformed, target = dataset[0]
    assert target["boxes"].tolist() == [[610, 600, 630, 620]]
    assert transformed[:, 600:620, 610:630].min().item() == 1
    assert transformed.sum().item() == 3 * 20 * 20
    cv2.imwrite(str(tmp_path / "image.png"), np.zeros_like(image))
    with pytest.raises(ValueError, match="differs from the pinned manifest"):
        dataset.load_image(0)


def test_training_dataset_rejects_final_test_before_reading_any_manifest(tmp_path):
    with pytest.raises(ValueError, match="only train/validation"):
        DeepPcbDataset(tmp_path, tmp_path, "test")


def test_checkpoint_rng_replays_augmentation_sampling_and_shuffle():
    set_seed(17)
    generator = torch.Generator().manual_seed(19)
    state = capture_rng(generator)
    expected = (random.random(), np.random.random(), torch.rand(4), torch.randperm(10, generator=generator))
    restore_rng(state, generator)
    actual = (random.random(), np.random.random(), torch.rand(4), torch.randperm(10, generator=generator))
    assert actual[:2] == expected[:2]
    torch.testing.assert_close(actual[2], expected[2], rtol=0, atol=0)
    torch.testing.assert_close(actual[3], expected[3], rtol=0, atol=0)


def test_asynchronous_device_failure_retains_the_entire_validation_population(monkeypatch):
    synchronization_calls = 0

    def synchronize(_):
        nonlocal synchronization_calls
        synchronization_calls += 1
        if synchronization_calls >= 2:
            raise RuntimeError("injected asynchronous CUDA error")

    class Model:
        def eval(self):
            return self

        def __call__(self, images):
            return [{"boxes": torch.tensor([[0.0, 0.0, 10.0, 10.0]]), "labels": torch.tensor([1]), "scores": torch.tensor([0.9])}]

    monkeypatch.setattr(train, "synchronize", synchronize)
    samples = [{"sampleId": name} for name in ("first", "second")]
    dataset = SimpleNamespace(rows=samples, truths=[{**row, "defects": [{"box": [0, 0, 10, 10], "classId": 1}]} for row in samples],
                              load_image=lambda index: torch.zeros(3, 8, 8))
    protocol = json.loads((Path(__file__).resolve().parents[2] / "training/evaluation-config.json").read_text())
    report, rows = train.validate(Model(), dataset, torch.device("cpu"), protocol)
    assert report["executionFailures"] == report["images"] == 2
    assert report["operatingPoint"]["fn"] == 2
    assert report["operatingPoint"]["tp"] == 0
    assert [row["execution"] for row in rows] == ["Failed", "Failed"]
    assert all(not row["defects"] for row in rows)
