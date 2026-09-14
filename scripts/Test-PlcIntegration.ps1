param(
    [ValidateSet('normal', 'duplicate-trigger', 'busy', 'lost-ack', 'reconnect', 'busy-during-capture', 'ack-crash', 'completion-gate')]
    [string[]] $Scenario = @('normal', 'duplicate-trigger', 'busy', 'lost-ack', 'reconnect', 'busy-during-capture', 'ack-crash', 'completion-gate'),
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $boardtraceRoot "artifacts/plc-integration/$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location $boardtraceRoot
try {
    dotnet build tools/BoardTrace.Plc.Probe -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw 'PLC integration probe build failed.' }
    foreach ($item in $Scenario) {
        dotnet run --project tools/BoardTrace.Plc.Probe -c Debug --no-build -- $item (Join-Path $OutputDirectory $item)
        if ($LASTEXITCODE -ne 0) { throw "PLC integration scenario failed: $item" }
    }
} finally { Pop-Location }
