$ErrorActionPreference = 'Stop'
$boardtraceRoot = Split-Path $PSScriptRoot -Parent
$download = Join-Path $boardtraceRoot '.local/SqlLocalDB.msi'
$log = Join-Path $boardtraceRoot 'artifacts/environment/localdb-install-elevated.log'
$expectedHash = 'ec43cc09e449aa33230891ee92296d8fe13f0c5f1d34e7343b00ad73907a1dc0'
# Official Visual Studio 2022 release catalog payload, retrieved 2026-09-13.
$url = 'https://download.visualstudio.microsoft.com/download/pr/0fc8bd99-c63f-47b9-96bc-2c8fe160dd44/ec43cc09e449aa33230891ee92296d8fe13f0c5f1d34e7343b00ad73907a1dc0/SqlLocalDB.msi'
New-Item -ItemType Directory -Force (Split-Path $download), (Split-Path $log) | Out-Null
if (-not (Test-Path $download)) {
    curl.exe -L --fail --silent --show-error $url -o $download
    if ($LASTEXITCODE -ne 0) { throw 'LocalDB download failed' }
}
if ((Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'LocalDB package hash does not match the pinned Microsoft catalog entry'
}
if ((Get-AuthenticodeSignature $download).Status -ne 'Valid') { throw 'Invalid LocalDB signature' }
$arguments = @('/i', ('"' + $download + '"'), '/qn', '/norestart', 'IACCEPTSQLLOCALDBLICENSETERMS=YES', '/L*v', ('"' + $log + '"'))
# MSI requires Windows administrator rights. Elevation uses the ordinary UAC prompt.
$setup = Start-Process msiexec.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -Wait
if ($setup.ExitCode -notin 0,3010) { throw "LocalDB installation failed ($($setup.ExitCode)); see $log" }
Write-Output "LocalDB installation completed, exit=$($setup.ExitCode)."
