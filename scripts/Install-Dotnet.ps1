$ErrorActionPreference = 'Stop'
$boardtraceRoot = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $boardtraceRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases.sdks | Where-Object version -eq $version | Select-Object -First 1
if (-not $sdk) { throw "Official release metadata does not contain SDK $version" }
$package = $sdk.files | Where-Object name -eq 'dotnet-sdk-win-x64.zip'
$download = Join-Path $boardtraceRoot '.local/dotnet-sdk.zip'
$destination = Join-Path $boardtraceRoot '.local/dotnet'
New-Item -ItemType Directory -Force $destination | Out-Null
curl.exe -L --fail --silent --show-error $package.url -o $download
if ($LASTEXITCODE -ne 0) { throw 'SDK download failed' }
if ((Get-FileHash $download -Algorithm SHA512).Hash.ToLowerInvariant() -ne $package.hash) {
    throw 'SDK SHA512 does not match official release metadata'
}
tar.exe -xf $download -C $destination
if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed' }
& (Join-Path $destination 'dotnet.exe') --info
