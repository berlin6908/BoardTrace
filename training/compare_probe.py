"""Compare the same exported model and image in Python ONNX Runtime and C#."""
import json
from pathlib import Path

import numpy as np

python = json.loads(Path("artifacts/environment/local-training-probe/probe.json").read_text(encoding="utf-8"))
csharp = json.loads(Path("artifacts/environment/onnx-csharp-probe.json").read_text(encoding="utf-8-sig"))
assert python["modelSha256"] == csharp["modelSha256"], "Compared models differ"
assert python["imageSha256"] == csharp["imageSha256"], "Compared input images differ"
np.testing.assert_array_equal(python["labels"], csharp["labels"])
np.testing.assert_allclose(python["boxes"], csharp["boxes"], atol=0.1, rtol=1e-4)
np.testing.assert_allclose(python["scores"], csharp["scores"], atol=1e-4, rtol=1e-3)
report = {"sample": python["input"], "modelSha256": python["modelSha256"],
    "detections": len(python["labels"]), "labelsEqual": True,
    "imageSha256": python["imageSha256"],
    "maximumBoxDelta": float(np.max(np.abs(np.array(python["boxes"]) - csharp["boxes"]), initial=0)),
    "maximumScoreDelta": float(np.max(np.abs(np.array(python["scores"]) - csharp["scores"]), initial=0)),
    "scope": "environment probe on one image; not final cross-language acceptance"}
Path("artifacts/environment/cross-language-probe.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
