#!/bin/bash
#SBATCH --job-name=boardtrace-resnet-probe
#SBATCH --account=aisc
#SBATCH --partition=aisc-batch
#SBATCH --constraint=ARCH:X86
#SBATCH --gres=gpu:1
#SBATCH --cpus-per-task=4
#SBATCH --mem=24G
#SBATCH --time=00:30:00
#SBATCH --output=artifacts/environment/hpi-probe-%j.log
set -euo pipefail
# Arguments: immutable bundle directory, shared results parent.
bundle_dir=$(realpath "$1")
results_parent=$(realpath "$2")
source "$bundle_dir/hpi-node-environment.sh" "$bundle_dir"
run_output="$results_parent/probe-$SLURM_JOB_ID"
mkdir "$run_output"
cp "$BOARDTRACE_NODE_WORK/environment-validation.json" "$BOARDTRACE_NODE_WORK/cuda-validation.json" "$BOARDTRACE_NODE_WORK/source-identity.txt" "$run_output/"
"$BOARDTRACE_PYTHON" training/train.py --device cuda --smoke-samples 4 --stop-after-epoch 1 --output "$run_output/training"
"$BOARDTRACE_PYTHON" training/train.py --device cuda --smoke-samples 4 --stop-after-epoch 2 --resume --output "$run_output/training"
"$BOARDTRACE_PYTHON" training/export.py --checkpoint "$run_output/training/best.pt" --output "$run_output/export" --samples 4
"$BOARDTRACE_PYTHON" - "$run_output" <<'PY'
import json
from pathlib import Path
import sys
output = Path(sys.argv[1])
summary = json.loads((output / "training/summary.json").read_text())
resumed = json.loads((output / "training/invocation-002.json").read_text())
exported = json.loads((output / "export/model.manifest.json").read_text())
assert summary["completedEpochs"] == 2
assert summary["kind"] == "training-smoke-not-quality-evaluation"
assert resumed["hardware"]["resumedFromEpoch"] == 1
assert exported["verificationSamples"] == 4
print(json.dumps({"kind": summary["kind"], "completedEpochs": 2, "resumedFromEpoch": 1,
                  "sourceCommit": resumed["hardware"]["gitCommit"], "modelSha256": exported["modelSha256"],
                  "onnxVerificationSamples": 4, "testSetUsed": False}, indent=2))
PY
