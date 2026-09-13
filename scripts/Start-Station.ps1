param(
    [string]$Station = 'STATION-01',
    [string]$Manifest = 'training/manifests/inputs/validation.jsonl',
    [string]$Database = "data/stations/$Station.db",
    [string]$Server = 'http://127.0.0.1:5180'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
Push-Location $boardtraceRoot
try {
    dotnet build src/BoardTrace.Station -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '工位构建失败。' }
    & "$boardtraceRoot/src/BoardTrace.Station/bin/Release/net10.0-windows/BoardTrace.Station.exe" --station $Station --manifest $Manifest --database $Database --data-root "$boardtraceRoot/data" --server $Server
} finally {
    Pop-Location
}
