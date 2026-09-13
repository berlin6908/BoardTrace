"""TorchVision detector with a seven-way (background + six defects) prediction head."""
import torch
from functools import partial
from torchvision.models.detection import (
    FasterRCNN_ResNet50_FPN_Weights,
    FasterRCNN,
)
from torchvision.models.detection.anchor_utils import AnchorGenerator
from torchvision.models.detection.backbone_utils import resnet_fpn_backbone
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
from torchvision.ops import FrozenBatchNorm2d


def build_model(pretrained=False):
    # Keep identical normalization and trainable layers when restoring a checkpoint.
    # TorchVision's convenience factory changes both when weights=None.
    backbone = resnet_fpn_backbone(backbone_name="resnet50", weights=None,
        norm_layer=partial(FrozenBatchNorm2d, eps=0.0), trainable_layers=3)
    model = FasterRCNN(backbone, num_classes=91, min_size=640, max_size=640,
        box_score_thresh=0.001, box_detections_per_img=100,
    )
    if pretrained:
        model.load_state_dict(FasterRCNN_ResNet50_FPN_Weights.COCO_V1.get_state_dict(progress=True, check_hash=True))
    channels = model.roi_heads.box_predictor.cls_score.in_features
    model.roi_heads.box_predictor = FastRCNNPredictor(channels, 7)
    # P2 starts at stride 4; use one small-object anchor scale on each pyramid level.
    model.rpn.anchor_generator = AnchorGenerator(((8,), (16,), (32,), (64,), (128,)), ((0.5, 1.0, 2.0),) * 5)
    return model


class OnnxDetector(torch.nn.Module):
    def __init__(self, detector):
        super().__init__()
        self.detector = detector

    def forward(self, images):
        result = self.detector([images[0]])[0]
        return result["boxes"], result["labels"], result["scores"]
