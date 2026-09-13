#!/bin/bash
#SBATCH --job-name=boardtrace-train
#SBATCH --account=aisc
#SBATCH --partition=aisc-batch
#SBATCH --constraint=ARCH:X86
#SBATCH --gres=gpu:1
#SBATCH --cpus-per-task=4
#SBATCH --mem=24G
#SBATCH --time=12:00:00
#SBATCH --output=artifacts/training/hpi-train-%j.log
set -euo pipefail
# Arguments: immutable bundle directory, shared training output, then trainer options.
bundle_dir=$(realpath "$1")
output_dir=$(realpath -m "$2")
shift 2
source "$bundle_dir/hpi-node-environment.sh" "$bundle_dir"
sha256sum training/train-config.json training/evaluation-config.json
"$BOARDTRACE_PYTHON" training/train.py --device cuda --output "$output_dir" "$@"
