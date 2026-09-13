$ErrorActionPreference = 'Stop'
$localdb = 'C:/Program Files/Microsoft SQL Server/150/Tools/Binn/SqlLocalDB.exe'
if (-not (Test-Path $localdb)) { throw 'Install LocalDB first with scripts/Install-LocalDb.ps1' }
$instances = & $localdb info
if ($instances -notcontains 'BoardTrace') {
    & $localdb create BoardTrace
    if ($LASTEXITCODE -ne 0) { throw 'Could not create BoardTrace LocalDB instance' }
}
& $localdb start BoardTrace
if ($LASTEXITCODE -ne 0) { throw 'Could not start BoardTrace LocalDB instance' }
& $localdb info BoardTrace
