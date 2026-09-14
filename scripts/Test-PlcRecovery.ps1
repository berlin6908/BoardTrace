param(
    [ValidateSet('kill-started', 'kill-completed', 'kill-ack')]
    [string[]] $Scenario = @('kill-started', 'kill-completed', 'kill-ack'),
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $boardtraceRoot "artifacts/plc-recovery/$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location $boardtraceRoot
try {
    dotnet build tools/BoardTrace.Plc.Probe -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw 'PLC recovery probe build failed.' }
    foreach ($item in $Scenario) {
        dotnet run --project tools/BoardTrace.Plc.Probe -c Debug --no-build -- $item (Join-Path $OutputDirectory $item)
        if ($LASTEXITCODE -ne 0) { throw "PLC process recovery failed: $item" }
    }
} finally { Pop-Location }
