param(
    [Parameter(Mandatory)][string]$ServerBin,
    [Parameter(Mandatory)][string]$StationBin,
    [Parameter(Mandatory)][string]$PublishedVersion,
    [Parameter(Mandatory)][string]$ModelDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '.local/dotnet')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '发布目录已存在；选择新的目录，不覆盖已验证发布物。' }
$version = Get-Content -LiteralPath $PublishedVersion -Raw | ConvertFrom-Json
$bundle = $version.bundle
$policyPath = Join-Path $repo 'training/release-policy.json'
$evaluationPath = Join-Path $repo 'training/evaluation-config.json'
$inputPath = Join-Path $repo 'training/manifests/inputs/validation.jsonl'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$evaluation = Get-Content -LiteralPath $evaluationPath -Raw | ConvertFrom-Json
$modelPath = Join-Path $ModelDirectory 'detector.onnx'
$expectedVision = $bundle.algorithmAssemblySha256
$expectedModel = $bundle.model.sha256
if ($bundle.definition.algorithm -ne 'PairedOnnx' -or $expectedVision -notmatch '^[a-f0-9]{64}$' -or
    $expectedModel -notmatch '^[a-f0-9]{64}$' -or $bundle.definition.modelSha256 -ne $expectedModel -or
    $policy.qualityTargetsFrozen -ne $true) { throw '需要已实际发布的固定 paired 版本和已冻结政策。' }
foreach ($name in @('minPrecision','minRecall','maxP95Ms')) {
    if ($policy.qualityTargets.$name -ne $bundle.releaseTargets.$name) { throw '冻结政策与发布版验收目标不一致。' }
}
$thresholds = $bundle.definition.thresholds
$classNames = @('open','short','mousebite','spur','copper','pinHole')
if ($evaluation.scoreThresholds.Count -ne 6) { throw '部署评分配置必须明确六类阈值。' }
for ($index = 0; $index -lt 6; $index++) {
    if ($thresholds.($classNames[$index]) -ne $evaluation.scoreThresholds[$index]) { throw '六类阈值与已发布版本不一致。' }
}
foreach ($folder in @($ServerBin, $StationBin)) {
    if ((Get-FileHash -LiteralPath (Join-Path $folder 'BoardTrace.Vision.dll')).Hash.ToLowerInvariant() -ne $expectedVision) {
        throw '检测程序集不是已经验证并发布的实际字节；不能通过重新计算一个新哈希绕过。'
    }
}
if ((Get-FileHash -LiteralPath $modelPath).Hash.ToLowerInvariant() -ne $expectedModel -or
    (Get-Item -LiteralPath $modelPath).Length -ne $bundle.model.byteLength -or
    (Get-FileHash -LiteralPath $inputPath).Hash.ToLowerInvariant() -ne $bundle.inputManifestSha256) {
    throw '模型或验证输入清单与已发布版本不同。'
}
if (-not (Test-Path -LiteralPath (Join-Path $ServerBin 'wwwroot/index.html'))) { throw '中央发布物缺少已构建 Web。' }
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App','Microsoft.WindowsDesktop.App')) {
    if (-not (Test-Path -LiteralPath (Join-Path $DotnetRoot "shared/$framework/10.0.12"))) { throw "缺少已验证运行时 $framework 10.0.12。" }
}
New-Item -ItemType Directory -Path $output | Out-Null

# Stage the already-tested framework-dependent binaries; never rebuild detector assets here.
foreach ($component in @(@{ Source = $ServerBin; Target = 'server' }, @{ Source = $StationBin; Target = 'station' })) {
    $target = Join-Path $output $component.Target
    New-Item -ItemType Directory -Path $target | Out-Null
    Get-ChildItem -LiteralPath $component.Source -File | Where-Object {
        ($_.Extension -in @('.dll','.exe','.pdb') -or $_.Name -like '*.deps.json' -or
            $_.Name -like '*.runtimeconfig.json' -or $_.Name -in @('appsettings.json','BoardTrace.Server.staticwebassets.endpoints.json')) -and
        $_.Name -notlike 'BoardTrace.Station.Smoke.*'
    } | Copy-Item -Destination $target
    Get-ChildItem -LiteralPath $component.Source -Directory | Where-Object {
        $_.Name -in @('runtimes','wwwroot') -or $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$'
    } | Copy-Item -Destination $target -Recurse
}
# The target database comes only from initialization's local state, never the source machine default.
$serverSettingsPath = Join-Path $output 'server/appsettings.json'
$serverSettings = Get-Content -LiteralPath $serverSettingsPath -Raw | ConvertFrom-Json
$serverSettings.PSObject.Properties.Remove('ConnectionStrings')
$serverSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $serverSettingsPath -Encoding utf8

$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
foreach ($name in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $DotnetRoot $name) -Destination $runtime -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $runtime 'host/fxr') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'host/fxr/10.0.12') -Destination (Join-Path $runtime 'host/fxr') -Recurse
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App','Microsoft.WindowsDesktop.App')) {
    $target = Join-Path $runtime "shared/$framework"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $DotnetRoot "shared/$framework/10.0.12") -Destination $target -Recurse
}
foreach ($directory in @('config','model','manifests/inputs','manifests/truth','data','scripts','simulator/tools/simulator','state')) {
    New-Item -ItemType Directory -Path (Join-Path $output $directory) -Force | Out-Null
}
Copy-Item -LiteralPath $PublishedVersion -Destination (Join-Path $output 'config/published-version.json')
Copy-Item -LiteralPath $policyPath -Destination (Join-Path $output 'config/release-policy.json')
Copy-Item -LiteralPath $evaluationPath -Destination (Join-Path $output 'config/evaluation-config.json')
Copy-Item -LiteralPath $modelPath -Destination (Join-Path $output 'model/detector.onnx')
Copy-Item -LiteralPath (Join-Path $ModelDirectory 'model.manifest.json') -Destination (Join-Path $output 'model/export.manifest.json')
Copy-Item -LiteralPath $inputPath -Destination (Join-Path $output 'manifests/inputs/validation.jsonl')
Copy-Item -LiteralPath (Join-Path $repo 'training/manifests/truth/validation.jsonl') -Destination (Join-Path $output 'manifests/truth/validation.jsonl')
$inputs = @(Get-Content -LiteralPath $inputPath | ForEach-Object { $_ | ConvertFrom-Json })
if ($inputs.Count -ne 200) { throw '验证人口必须为原固定200张。' }
$copiedImages = @{}
foreach ($input in $inputs) {
    foreach ($kind in @('image','reference')) {
        $relative = $input.$kind
        if ($copiedImages.ContainsKey($relative)) { continue }
        $dataRoot = [IO.Path]::GetFullPath((Join-Path $repo 'data')) + [IO.Path]::DirectorySeparatorChar
        $source = [IO.Path]::GetFullPath((Join-Path $dataRoot $relative))
        if (-not $source.StartsWith($dataRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '输入图像路径超出数据目录。' }
        if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $input.($kind + 'Sha256')) { throw "图像内容与清单不同：$relative" }
        $target = Join-Path (Join-Path $output 'data') $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        $copiedImages[$relative] = $true
    }
}
foreach ($name in @('Initialize-Release.ps1','Start-ReleaseServer.ps1','Start-ReleaseStation.ps1','Install-ReleaseSimulator.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $output 'scripts')
}
Get-ChildItem -LiteralPath (Join-Path $repo 'tools/simulator') -Filter '*.py' -File |
    Copy-Item -Destination (Join-Path $output 'simulator/tools/simulator')
Set-Content -LiteralPath (Join-Path $output 'simulator/requirements.txt') -Value 'pymodbus==3.11.1' -Encoding ascii
$inputs | ForEach-Object { @{ sampleId = $_.sampleId } | ConvertTo-Json -Compress } |
    Set-Content -LiteralPath (Join-Path $output 'simulator/samples.jsonl') -Encoding utf8
[ordered]@{ name = $bundle.name; definition = $bundle.definition; targets = $bundle.targets } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'config/draft-request.json') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); bytes = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
[ordered]@{ packagedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); platform = 'win-x64'; runtimeVersion = '10.0.12';
    sourcePublishedVersionId = $bundle.versionId; sourceBundleHash = $version.bundleHash;
    visionSha256 = $expectedVision; modelSha256 = $expectedModel; files = $files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
Write-Output "发布目录已准备：$output；未启动服务、未初始化数据库、未执行检测。"
