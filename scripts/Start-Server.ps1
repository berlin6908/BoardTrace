param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
& "$PSScriptRoot/Initialize-Database.ps1"
Push-Location $boardtraceRoot
try {
    if (-not $SkipBuild) {
        & "$PSScriptRoot/Build-Web.ps1"
        dotnet build src/BoardTrace.Server -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw '中央服务构建失败。' }
    }
    $serverExecutable = Join-Path $boardtraceRoot 'src/BoardTrace.Server/bin/Release/net10.0/BoardTrace.Server.exe'
    if (-not (Test-Path -LiteralPath $serverExecutable)) { throw '中央服务尚未构建，请先不带 -SkipBuild 运行。' }
    $env:RecipeValidation__DataRoot = (Resolve-Path -LiteralPath (Join-Path $boardtraceRoot 'data')).Path
    $env:RecipeValidation__ManifestRoot = (Resolve-Path -LiteralPath (Join-Path $boardtraceRoot 'training/manifests')).Path
    $env:RecipePublication__ReleasePolicyPath = Join-Path $boardtraceRoot 'training/release-policy.json'
    & $serverExecutable --urls http://127.0.0.1:5180
    if ($LASTEXITCODE -ne 0) { throw "中央服务退出，代码 $LASTEXITCODE。" }
} finally {
    Pop-Location
}
