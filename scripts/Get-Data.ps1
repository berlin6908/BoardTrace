$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$revision = '08e98c4db5922613fb97176eb3d6497d48260cb1'
if (-not (Test-Path 'data/DeepPCB/.git')) {
    git clone https://github.com/tangsanli5201/DeepPCB.git data/DeepPCB
    if ($LASTEXITCODE -ne 0) { throw 'DeepPCB download failed' }
}
git -C data/DeepPCB checkout --detach $revision
if ($LASTEXITCODE -ne 0) { throw 'DeepPCB revision checkout failed' }
Write-Output "DeepPCB pinned at $revision. Research use only; see docs/data.md."
