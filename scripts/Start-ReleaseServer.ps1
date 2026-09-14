param([string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$configuration = Get-Content -LiteralPath (Join-Path $package 'state/server.json') -Raw | ConvertFrom-Json
$env:ConnectionStrings__BoardTrace = $configuration.connectionString
$env:RecipeValidation__DataRoot = Join-Path $package 'data'
$env:RecipeValidation__ManifestRoot = Join-Path $package 'manifests'
$env:RecipePublication__ReleasePolicyPath = Join-Path $package 'config/release-policy.json'
& (Join-Path $package 'runtime/dotnet.exe') (Join-Path $package 'server/BoardTrace.Server.dll') --urls $configuration.url
if ($LASTEXITCODE -ne 0) { throw "中央服务退出，代码 $LASTEXITCODE。" }
