$ErrorActionPreference = 'Stop'
$boardtraceRoot = Split-Path $PSScriptRoot -Parent
Push-Location (Join-Path $boardtraceRoot 'src/boardtrace-web')
try {
    npm ci --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Web 依赖安装失败。' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Web 生产构建失败。' }
} finally {
    Pop-Location
}
