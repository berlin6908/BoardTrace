"""TorchVision detector with a seven-way (background + six defects) prediction head."""
import torch
from torchvision.models.detection import (
    FasterRCNN_MobileNet_V3_Large_FPN_Weights,
    fasterrcnn_mobilenet_v3_large_fpn,
)
from torchvision.models.detection.anchor_utils import AnchorGenerator
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor


def build_model(pretrained=False):
    weights = FasterRCNN_MobileNet_V3_Large_FPN_Weights.COCO_V1 if pretrained else None
    model = fasterrcnn_mobilenet_v3_large_fpn(
        weights=weights, weights_backbone=None, min_size=640, max_size=640,
        box_score_thresh=0.001, box_detections_per_img=100,
    )
    channels = model.roi_heads.box_predictor.cls_score.in_features
    model.roi_heads.box_predictor = FastRCNNPredictor(channels, 7)
    # DeepPCB defects are much smaller than COCO objects; retain 15 anchors per location.
    model.rpn.anchor_generator = AnchorGenerator(((8, 16, 32, 64, 128),) * 3, ((0.5, 1.0, 2.0),) * 3)
    return model


class OnnxDetector(torch.nn.Module):
    def __init__(self, detector):
        super().__init__()
        self.detector = detector

    def forward(self, images):
        result = self.detector([images[0]])[0]
        return result["boxes"], result["labels"], result["scores"]
