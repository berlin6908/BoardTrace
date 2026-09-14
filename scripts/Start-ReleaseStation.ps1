param(
    [string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent),
    [ValidateSet('STATION-01', 'STATION-02')][string]$Station = 'STATION-01'
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$configuration = Get-Content -LiteralPath (Join-Path $package 'state/server.json') -Raw | ConvertFrom-Json
& (Join-Path $package 'runtime/dotnet.exe') (Join-Path $package 'station/BoardTrace.Station.dll') `
    --station $Station --data-root (Join-Path $package 'data') --manifest (Join-Path $package 'manifests/inputs/validation.jsonl') `
    --database (Join-Path $package "state/stations/$Station.db") --server $configuration.url `
    --credentials (Join-Path $package "state/stations/$Station.json")
if ($LASTEXITCODE -ne 0) { throw "工位退出，代码 $LASTEXITCODE。" }
