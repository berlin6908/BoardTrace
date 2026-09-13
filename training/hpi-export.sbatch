#!/bin/bash
#SBATCH --job-name=boardtrace-export-2542605
#SBATCH --account=aisc
#SBATCH --partition=aisc-batch
#SBATCH --qos=aisc
#SBATCH --constraint=ARCH:X86
#SBATCH --cpus-per-task=4
#SBATCH --mem=24G
#SBATCH --time=00:45:00
#SBATCH --output=artifacts/training/hpi-export-%j.log
set -euo pipefail
# Arguments: verified training bundle, new results parent, selected best.pt, its SHA-256.
bundle_dir=$(realpath "$1")
results_parent=$(realpath "$2")
checkpoint_path=$(realpath "$3")
expected_sha=$4
[[ "$expected_sha" =~ ^[0-9a-f]{64}$ ]]
test -n "${SLURM_JOB_ID:?An allocated Slurm job is required}"
node_root=$(realpath "${SLURM_SCRATCH:?The HPI job-local scratch directory is required}")
test "$(uname -m)" = x86_64
df -hT "$node_root"
case "$(stat -f -c %T "$node_root")" in
    ext2/ext3|xfs|btrfs) ;;
    *) printf 'Expected a local disk: %s\n' "$node_root" >&2; exit 1 ;;
esac
available=$(df -B1 --output=avail "$node_root" | tail -1 | tr -d ' ')
test "$available" -ge 21474836480
BOARDTRACE_NODE_WORK=$(mktemp -d "$node_root/boardtrace-export-$SLURM_JOB_ID-XXXXXX")
cp "$bundle_dir/environment.tar.gz" "$bundle_dir/source.tar.gz" "$bundle_dir/data.tar.gz" "$bundle_dir/SHA256SUMS" "$BOARDTRACE_NODE_WORK/"
cd "$BOARDTRACE_NODE_WORK"
sha256sum -c SHA256SUMS
tar -xzf environment.tar.gz
mkdir source
tar -xzf source.tar.gz -C source
tar -xzf data.tar.gz -C source
BOARDTRACE_PYTHON="$BOARDTRACE_NODE_WORK/environment/bin/python"
test -x "$BOARDTRACE_PYTHON"
export PYTHONUNBUFFERED=1
export OMP_NUM_THREADS="$SLURM_CPUS_PER_TASK"
export OPENBLAS_NUM_THREADS="$SLURM_CPUS_PER_TASK"
"$BOARDTRACE_PYTHON" "$BOARDTRACE_NODE_WORK/verify.py" > "$BOARDTRACE_NODE_WORK/environment-validation.json"
cat "$BOARDTRACE_NODE_WORK/environment-validation.json"
cd "$BOARDTRACE_NODE_WORK/source"
test "$(git rev-parse HEAD)" = "$(cat "$bundle_dir/SOURCE_COMMIT")"
test -z "$(git status --porcelain)"
{
    git rev-parse HEAD
    git remote get-url origin
    sha256sum training/model.py training/train.py training/dataset.py training/export.py
} > "$BOARDTRACE_NODE_WORK/source-identity.txt"
run_output="$results_parent/export-$SLURM_JOB_ID"
mkdir "$run_output"
cp "$BOARDTRACE_NODE_WORK/environment-validation.json" "$BOARDTRACE_NODE_WORK/source-identity.txt" "$run_output/"
cp "$checkpoint_path" "$BOARDTRACE_NODE_WORK/best.pt"
printf '%s  %s\n' "$expected_sha" "$BOARDTRACE_NODE_WORK/best.pt" | sha256sum -c -
cp "$(dirname "$checkpoint_path")/summary.json" "$run_output/training-summary.json"
cp "$(dirname "$checkpoint_path")/history.json" "$run_output/training-history.json"
"$BOARDTRACE_PYTHON" - "$BOARDTRACE_NODE_WORK/best.pt" "$expected_sha" "$checkpoint_path" "$run_output" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import sys
import torch

checkpoint, expected_sha, original, output = sys.argv[1:]
output = Path(output)
summary = json.loads((output / "training-summary.json").read_text())
history = json.loads((output / "training-history.json").read_text())
assert summary["kind"] == "development-training"
assert summary["completedEpochs"] == summary["plannedEpochs"] == 30
assert summary["testSetUsed"] is False
assert [row["epoch"] for row in history] == list(range(1, 31))
assert all(row["training"]["samples"] == 800 and row["validation"]["images"] == 200
           and row["validation"]["executionFailures"] == 0 for row in history)
eligible = [row for row in history if row["eligibleForSelection"]]
selected = max(eligible, key=lambda row: row["validation"]["map50To95"])
assert selected["epoch"] == summary["bestEpoch"]
assert selected["validation"]["map50To95"] == summary["bestMap50To95"]
state = torch.load(checkpoint, map_location="cpu", weights_only=False)
assert state["epoch"] == selected["epoch"]
assert state["validation"] == selected["validation"]
assert state["identity"]["kind"] == "development-training"
assert state["identity"]["config"]["epochs"] == 30
for filename, expected in state["identity"]["sourceSha256"].items():
    assert hashlib.sha256((Path("training") / filename).read_bytes()).hexdigest() == expected, filename
record = {"trainingSlurmJobId": "2542605", "exportSlurmJobId": os.environ["SLURM_JOB_ID"],
          "checkpointOriginalPath": original, "checkpointSha256": expected_sha,
          "checkpointEpoch": state["epoch"], "completedTrainingEpochs": 30,
          "bestMap50To95": summary["bestMap50To95"], "selectionMetric": summary["selectionMetric"],
          "verificationSplit": "validation", "verificationSamples": 200,
          "testSetUsed": False, "qualityApproved": False}
(output / "selected-checkpoint.json").write_text(json.dumps(record, indent=2))
print(json.dumps(record, indent=2))
PY
"$BOARDTRACE_PYTHON" training/export.py --checkpoint "$BOARDTRACE_NODE_WORK/best.pt" --output "$run_output/model" --samples 200
"$BOARDTRACE_PYTHON" - "$run_output/model" <<'PY'
import json
from pathlib import Path
import sys
output = Path(sys.argv[1])
manifest = json.loads((output / "model.manifest.json").read_text())
assert manifest["kind"] == "development-training"
assert manifest["verificationSamples"] == 200
assert manifest["qualityApproved"] is False
print(json.dumps({"exportComplete": True, "checkpointEpoch": manifest["checkpointEpoch"],
                  "modelSha256": manifest["modelSha256"], "verificationSamples": 200,
                  "qualityApproved": False}, indent=2))
PY
sha256sum "$run_output/model/detector.onnx" "$run_output/model/model.manifest.json" "$run_output/model/comparison.json" > "$run_output/EXPORT_SHA256SUMS"
