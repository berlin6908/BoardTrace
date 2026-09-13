#!/bin/bash
# Source this file inside an HPI Slurm job: <bundle directory>.
set -euo pipefail
bundle_dir=$(realpath "$1")
test -n "${SLURM_JOB_ID:?An allocated Slurm job is required}"
node_root=$(realpath "${SLURM_SCRATCH:?The HPI job-local scratch directory is required}")
test "$(uname -m)" = x86_64
printf 'SLURM_SCRATCH=%s\n' "$node_root"
df -hT "$node_root"
case "$(stat -f -c %T "$node_root")" in
    ext2/ext3|xfs|btrfs) ;;
    *) printf 'Expected a local disk, received: %s\n' "$node_root" >&2; return 1 ;;
esac
available=$(df -B1 --output=avail "$node_root" | tail -1 | tr -d ' ')
test "$available" -ge 21474836480
export BOARDTRACE_NODE_WORK
BOARDTRACE_NODE_WORK=$(mktemp -d "$node_root/boardtrace-$SLURM_JOB_ID-XXXXXX")
cp "$bundle_dir/environment.tar.gz" "$bundle_dir/source.tar.gz" "$bundle_dir/data.tar.gz" "$bundle_dir/SHA256SUMS" "$BOARDTRACE_NODE_WORK/"
cd "$BOARDTRACE_NODE_WORK"
sha256sum -c SHA256SUMS
tar -xzf environment.tar.gz
mkdir source
tar -xzf source.tar.gz -C source
tar -xzf data.tar.gz -C source
export BOARDTRACE_PYTHON="$BOARDTRACE_NODE_WORK/environment/bin/python"
test -x "$BOARDTRACE_PYTHON"
export TORCH_HOME="$BOARDTRACE_NODE_WORK/source/data/torch"
export PYTHONUNBUFFERED=1
export OMP_NUM_THREADS="${SLURM_CPUS_PER_TASK}"
export OPENBLAS_NUM_THREADS="${SLURM_CPUS_PER_TASK}"
"$BOARDTRACE_PYTHON" "$BOARDTRACE_NODE_WORK/verify.py" > "$BOARDTRACE_NODE_WORK/environment-validation.json"
cat "$BOARDTRACE_NODE_WORK/environment-validation.json"
nvidia-smi
"$BOARDTRACE_PYTHON" - <<'PY' > "$BOARDTRACE_NODE_WORK/cuda-validation.json"
import json
import torch
from torchvision.ops import nms
assert torch.cuda.is_available(), "The allocated GPU is unavailable to PyTorch"
values = torch.randn((256, 256), device="cuda", requires_grad=True)
values.square().mean().backward()
boxes = torch.tensor([[0., 0., 10., 10.], [1., 1., 9., 9.], [20., 20., 25., 25.]], device="cuda")
keep = nms(boxes, torch.tensor([0.9, 0.8, 0.7], device="cuda"), 0.5)
torch.cuda.synchronize()
assert keep.cpu().tolist() == [0, 2]
print(json.dumps({"gpu": torch.cuda.get_device_name(), "gpuMemoryBytes": torch.cuda.get_device_properties(0).total_memory,
                  "cudaRuntime": torch.version.cuda, "tensorBackward": "passed", "torchvisionCudaNms": "passed"}, indent=2))
PY
cat "$BOARDTRACE_NODE_WORK/cuda-validation.json"
cd "$BOARDTRACE_NODE_WORK/source"
test "$(git rev-parse HEAD)" = "$(cat "$bundle_dir/SOURCE_COMMIT")"
{
    git rev-parse HEAD
    git remote get-url origin
    git status --short
    sha256sum training/model.py training/train.py training/dataset.py training/export.py training/hpi-*.sh training/hpi-*.sbatch
} > "$BOARDTRACE_NODE_WORK/source-identity.txt"
cat "$BOARDTRACE_NODE_WORK/source-identity.txt"
