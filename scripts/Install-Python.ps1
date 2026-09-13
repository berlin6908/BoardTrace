$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
uv venv --python 3.11 .venv
if ($LASTEXITCODE -ne 0) { throw 'Python environment creation failed' }
uv pip install --python .venv/Scripts/python.exe torch==2.7.1 torchvision==0.22.1 --index-url https://download.pytorch.org/whl/cu126
if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed' }
uv pip install --python .venv/Scripts/python.exe -r training/requirements.in
if ($LASTEXITCODE -ne 0) { throw 'Training dependencies installation failed' }
