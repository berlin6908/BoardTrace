param(
    [string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][ValidatePattern('^BoardTrace_[A-Za-z0-9_]+$')][string]$DatabaseName,
    [ValidateRange(1024, 65535)][int]$Port = 5190
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($required in @('release.json','runtime/dotnet.exe','server/BoardTrace.Server.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required))) { throw "发布包缺少 $required。" }
}
$configurationPath = Join-Path $package 'state/server.json'
if (Test-Path -LiteralPath $configurationPath) { throw '此发布目录已经初始化，请使用 Start-ReleaseServer.ps1；不覆盖已有运行配置。' }
$localdb = 'C:/Program Files/Microsoft SQL Server/150/Tools/Binn/SqlLocalDB.exe'
if (-not (Test-Path -LiteralPath $localdb)) { throw '先安装 SQL Server 2019 Express LocalDB；安装说明见本地交付文档。' }
$instances = & $localdb info
if ($LASTEXITCODE -ne 0) { throw '无法列出 LocalDB 实例。' }
if ($instances -notcontains 'BoardTrace') {
    & $localdb create BoardTrace
    if ($LASTEXITCODE -ne 0) { throw '无法创建 BoardTrace LocalDB 实例。' }
}
& $localdb start BoardTrace
if ($LASTEXITCODE -ne 0) { throw '无法启动 BoardTrace LocalDB 实例。' }
$connection = "Server=(localdb)\BoardTrace;Database=$DatabaseName;Integrated Security=true;TrustServerCertificate=true"
$env:ConnectionStrings__BoardTrace = $connection
$dotnet = Join-Path $package 'runtime/dotnet.exe'
$accounts = Join-Path $package 'state/development-accounts.json'
# Uses the application's Identity initialization. No reset/delete option exists in this script.
& $dotnet (Join-Path $package 'server/BoardTrace.Server.dll') --initialize-development true --development-accounts $accounts
if ($LASTEXITCODE -ne 0) { throw '新数据库初始化失败；保留数据库和凭据供诊断，未删除任何数据库。' }
[ordered]@{ database = $DatabaseName; connectionString = $connection; url = "http://127.0.0.1:$Port" } |
    ConvertTo-Json | Set-Content -LiteralPath $configurationPath -Encoding utf8
Write-Output "初始化完成：$DatabaseName；凭据只在 $accounts；使用 Start-ReleaseServer.ps1 启动。"
