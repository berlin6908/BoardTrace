#!/bin/bash
#SBATCH --job-name=boardtrace-probe
#SBATCH --account=aisc
#SBATCH --partition=aisc-batch
#SBATCH --gres=gpu:1
#SBATCH --cpus-per-task=4
#SBATCH --mem=16G
#SBATCH --time=00:30:00
#SBATCH --output=artifacts/environment/hpi-probe-%j.log
set -euo pipefail
cd "${SLURM_SUBMIT_DIR}"
export TORCH_HOME="$PWD/data/torch"
export OMP_NUM_THREADS=4
nvidia-smi
sha256sum training/model.py training/probe.py
.venv/bin/python training/probe.py
