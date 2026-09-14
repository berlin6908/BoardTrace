param(
    [string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$PythonExecutable
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$environment = Join-Path $package 'simulator/.venv'
& $PythonExecutable -m venv $environment
if ($LASTEXITCODE -ne 0) { throw '模拟器 Python 环境创建失败。' }
& (Join-Path $environment 'Scripts/python.exe') -m pip install --disable-pip-version-check -r (Join-Path $package 'simulator/requirements.txt')
if ($LASTEXITCODE -ne 0) { throw '模拟器依赖安装失败。' }
