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
cd "${SLURM_SUBMIT_DIR}"
export TORCH_HOME="$PWD/data/torch"
export PYTHONUNBUFFERED=1
export OMP_NUM_THREADS=4
nvidia-smi
sha256sum training/train.py training/model.py training/dataset.py training/detection_evaluation.py training/train-config.json
# Extra arguments may select an output directory and --resume; no test split is accepted.
.venv/bin/python training/train.py "$@"
