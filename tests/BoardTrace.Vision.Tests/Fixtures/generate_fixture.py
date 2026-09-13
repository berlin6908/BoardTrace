"""Regenerate the tiny pixel-dependent ONNX fixture; requires the training ONNX package."""
from pathlib import Path

from onnx import TensorProto, checker, helper, save

nodes = [
    helper.make_node("GatherND", ["images", "pixel_indices"], ["rgb"]),
    helper.make_node("Concat", ["rgb", "rgb"], ["scores"], axis=0),
    helper.make_node("Identity", ["fixed_boxes"], ["boxes"]),
    helper.make_node("Identity", ["fixed_labels"], ["labels"]),
]
graph = helper.make_graph(
    nodes,
    "rgb_pixel_detector",
    [helper.make_tensor_value_info("images", TensorProto.FLOAT, [1, 3, 640, 640])],
    [
        helper.make_tensor_value_info("boxes", TensorProto.FLOAT, [6, 4]),
        helper.make_tensor_value_info("labels", TensorProto.INT64, [6]),
        helper.make_tensor_value_info("scores", TensorProto.FLOAT, [6]),
    ],
    initializer=[
        helper.make_tensor("pixel_indices", TensorProto.INT64, [3, 4],
                           [0, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0]),
        helper.make_tensor("fixed_boxes", TensorProto.FLOAT, [6, 4],
                           [10.25, 20.5, 30.25, 50.5] * 6),
        helper.make_tensor("fixed_labels", TensorProto.INT64, [6], range(1, 7)),
    ],
)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=10)
checker.check_model(model)
save(model, Path(__file__).with_name("pixel-detector.onnx"))
