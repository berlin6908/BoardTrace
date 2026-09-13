param([switch]$ResetDatabase)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
& "$PSScriptRoot/Initialize-Database.ps1"
Push-Location $boardtraceRoot
try {
    $env:ConnectionStrings__BoardTrace = 'Server=(localdb)\BoardTrace;Database=BoardTrace;Integrated Security=true;TrustServerCertificate=true'
    dotnet build src/BoardTrace.Server -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
    $arguments = @('--initialize-development', 'true', '--development-accounts', (Join-Path $boardtraceRoot '.local/development-accounts.json'))
    if ($ResetDatabase) { $arguments += @('--reset-database', 'true') }
    & "$boardtraceRoot/src/BoardTrace.Server/bin/Release/net10.0/BoardTrace.Server.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Development initialization failed.' }
    Write-Output 'Development accounts initialized in .local/development-accounts.json and .local/stations/.'
} finally {
    Pop-Location
}
