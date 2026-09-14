param(
    [Parameter(Mandatory)][string] $Station01Context,
    [Parameter(Mandatory)][string] $Station02Context,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $StationBinary,
    [int] $MinimumMinutes = 120,
    [int] $MinimumTriggers = 10000,
    [int] $MaximumMinutes = 720
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Enter-Environment.ps1"
$root = $boardtraceRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$contexts = @($Station01Context, $Station02Context) | ForEach-Object {
    Get-Content -LiteralPath ([IO.Path]::GetFullPath($_)) -Raw | ConvertFrom-Json
}
if ($contexts.Count -ne 2 -or $MinimumMinutes -lt 0 -or $MinimumTriggers -lt 1 -or $MaximumMinutes -le $MinimumMinutes) {
    throw '需要两个工位、正数触发数量，以及大于最短时长的最长时长。'
}
if ($contexts[0].Options.StationId -eq $contexts[1].Options.StationId -or
    $contexts[0].BatchId -eq $contexts[1].BatchId -or
    $contexts[0].PlcPort -eq $contexts[1].PlcPort -or
    $contexts[0].ProductPrefix -eq $contexts[1].ProductPrefix -or
    $contexts[0].Options.DatabasePath -eq $contexts[1].Options.DatabasePath -or
    ($contexts[0].ProductionCount + $contexts[1].ProductionCount) -lt $MinimumTriggers) {
    throw '两工位须有不同身份、批次、端口、SQLite，计划数量须满足总触发数。'
}
if (Test-Path -LiteralPath $output) { throw "证据目录已存在：$output" }
New-Item -ItemType Directory -Path $output | Out-Null
$runtime = @()
for ($i = 0; $i -lt 2; $i++) {
    $station = Join-Path $output "station-$($i + 1)"
    New-Item -ItemType Directory -Path $station | Out-Null
    $contexts[$i].Output = $station
    $runtimePath = Join-Path $station 'context.json'
    if (Test-Path -LiteralPath $contexts[$i].Options.DatabasePath) {
        throw "工位SQLite已存在，不允许覆盖：$($contexts[$i].Options.DatabasePath)"
    }
    $runtime += [pscustomobject]@{ Context = $contexts[$i]; Path = $runtimePath; Folder = $station }
}
$dotnet = Join-Path $root '.local/dotnet/dotnet.exe'
$python = Join-Path $root '.venv/Scripts/python.exe'
$stationDll = [IO.Path]::GetFullPath($StationBinary)
foreach ($path in @($dotnet, $python, $stationDll)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少已构建运行依赖：$path" }
}
$binary = Split-Path $stationDll
$binaryHashes = @{}
foreach ($name in @('BoardTrace.Station.Smoke.dll','BoardTrace.Station.dll','BoardTrace.Station.Core.dll',
                   'BoardTrace.Contracts.dll','BoardTrace.Vision.dll')) {
    $path = Join-Path $binary $name
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少固定二进制：$path" }
    $binaryHashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$sourceHead = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法读取源码 Git HEAD。' }
$simulatorHashes = @{}
foreach ($source in (Get-ChildItem -LiteralPath (Join-Path $root 'tools/simulator') -File -Filter '*.py')) {
    $simulatorHashes[$source.Name] = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[pscustomobject]@{ atUtc = [DateTimeOffset]::UtcNow; stationBinary = $stationDll; hashes = $binaryHashes;
    sourceGitHead = $sourceHead; simulatorHashes = $simulatorHashes;
    python = $python; stationContexts = @($runtime | ForEach-Object { $_.Path }) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'run-inputs.json') -Encoding utf8
$started = [DateTimeOffset]::UtcNow
$endAt = $started.AddMinutes($MinimumMinutes)
foreach ($item in $runtime) {
    $item.Context.MinimumEndUtc = $endAt.ToString('O')
    $item.Context | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $item.Path -Encoding utf8
}

function Start-Owned([string] $file, [string[]] $arguments, [string] $folder, [string] $name) {
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "无法启动 $name" }
    $stdout = [IO.File]::Create((Join-Path $folder "$name.stdout.log"))
    $stderr = [IO.File]::Create((Join-Path $folder "$name.stderr.log"))
    return [pscustomobject]@{
        Name = $name; Folder = $folder; File = $file; Arguments = @($arguments);
        Process = $process; Stdout = $stdout; Stderr = $stderr
        OutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        ErrTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
    }
}

$owned = @()
try {
    foreach ($item in $runtime) {
        $c = $item.Context
        $state = Join-Path $item.Folder 'simulator.db'
        $events = Join-Path $item.Folder 'plc-events.jsonl'
        $arguments = @('-m','tools.simulator','--scenario','normal','--host','127.0.0.1',
            '--port',"$($c.PlcPort)",'--count',"$($c.ProductionCount)",'--product-prefix',"$($c.ProductPrefix)",
            '--samples',"$($c.SamplesPath)",'--state',$state,'--output',$events,'--timeout','60')
        $owned += Start-Owned $python $arguments $item.Folder 'simulator'
    }
    foreach ($item in $runtime) {
        $owned += Start-Owned $dotnet @($stationDll,'--scope','system-station','--context',$item.Path) $item.Folder 'station'
    }
    $owned | ForEach-Object { [pscustomobject]@{ name = $_.Name; folder = $_.Folder; pid = $_.Process.Id;
        file = $_.File; arguments = $_.Arguments; startedAtUtc = $started } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'processes.json') -Encoding utf8
    $resourceLog = Join-Path $output 'resources.jsonl'
    while ($true) {
        $now = [DateTimeOffset]::UtcNow
        if ($now -gt $started.AddMinutes($MaximumMinutes)) { throw '双工位运行超过明确最长时长。' }
        $allExited = $true
        foreach ($item in $owned) {
            $process = $item.Process
            $process.Refresh()
            if (-not $process.HasExited) { $allExited = $false }
            [pscustomobject]@{ atUtc = $now; name = $item.Name; folder = $item.Folder; pid = $process.Id;
                exited = $process.HasExited; workingSetBytes = if ($process.HasExited) { $null } else { $process.WorkingSet64 };
                privateBytes = if ($process.HasExited) { $null } else { $process.PrivateMemorySize64 };
                cpuSeconds = if ($process.HasExited) { $null } else { $process.TotalProcessorTime.TotalSeconds } } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath $resourceLog -Encoding utf8
            if ($process.HasExited -and $process.ExitCode -ne 0) { throw "$($item.Name) PID $($process.Id) 退出码 $($process.ExitCode)。" }
        }
        if ($allExited) { break }
        Start-Sleep -Seconds 10
    }
    $results = @($runtime | ForEach-Object {
        Get-Content -LiteralPath (Join-Path $_.Folder 'station-result.json') -Raw | ConvertFrom-Json
    })
    $finished = [DateTimeOffset]::UtcNow
    $actual = [int]($results[0].production + $results[1].production)
    if ($actual -lt $MinimumTriggers -or ($finished - $started).TotalMinutes -lt $MinimumMinutes -or
        @($results | Where-Object { $_.pending -ne 0 -or $null -ne $_.unacknowledged }).Count -ne 0) {
        throw '实际时间、真实生产检测数或最终回执/ACK状态未达验收条件。'
    }
    [pscustomobject]@{ startedAtUtc = $started; finishedAtUtc = $finished; durationMinutes = ($finished-$started).TotalMinutes;
        minimumTriggers = $MinimumTriggers; actualProduction = $actual; stations = $results } |
        ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $output 'system-result.json') -Encoding utf8
    if (@($results | Where-Object { $_.failed -ne 0 }).Count -ne 0) {
        throw '存在图像采集/模型执行等技术失败；已保存逐件证据，调查修复后重验。'
    }
} finally {
    foreach ($item in $owned) {
        if (-not $item.Process.HasExited) { $item.Process.Kill($true); $item.Process.WaitForExit() }
        $item.OutTask.GetAwaiter().GetResult()
        $item.ErrTask.GetAwaiter().GetResult()
        $item.Stdout.Dispose()
        $item.Stderr.Dispose()
        $item.Process.Dispose()
    }
}
