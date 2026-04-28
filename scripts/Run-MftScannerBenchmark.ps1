param(
    [string]$Configuration = "Debug",
    [string]$PathPrefix = "$env:USERPROFILE\Desktop",
    [int]$MaxResults = 500,
    [int]$Repeat = 1,
    [int]$ReadyTimeoutSeconds = 180,
    [int]$SearchTimeoutSeconds = 60,
    [ValidateSet("SharedHost", "InProcess")]
    [string]$Backend = "SharedHost",
    [switch]$NoRestartHost,
    [string]$OutputDirectory = ".\artifacts\mft-benchmark"
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path -Parent $scriptPath) "..")).Path
}

function Resolve-MSBuild {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw "MSBuild.exe was not found."
}

function Stop-MftScannerHost {
    Get-CimInstance Win32_Process -Filter "Name = 'MftScanner.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $processId = $_.ProcessId
            try {
                Stop-Process -Id $processId -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Failed to stop MftScanner.exe pid=${processId}: $($_.Exception.Message)"
            }
        }
}

function Get-ProcessMemorySnapshot {
    Get-Process | Where-Object { $_.ProcessName -match 'MftScanner|Everything|PackageManager' } |
        Select-Object Id, ProcessName, Path,
            @{ Name = "WorkingSetMB"; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
            @{ Name = "PrivateMB"; Expression = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } }
}

function Parse-PathPrefilterEvents {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) {
        return @()
    }

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($match in Select-String -Path $LogPath -Pattern "\[PATH PREFILTER\]" -ErrorAction SilentlyContinue) {
        $line = $match.Line
        if ($line -notmatch "^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)") {
            continue
        }

        $time = [datetime]$Matches.time
        if ($time -lt $Since) {
            continue
        }

        $outcome = if ($line -match "outcome=(?<v>\S+)") { $Matches.v } else { "" }
        $elapsed = if ($line -match "elapsedMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $candidate = if ($line -match "candidateCount=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $directories = if ($line -match "directories=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $path = if ($line -match "pathPrefix=(?<v>.+)$") { $Matches.v } else { "" }

        $events.Add([pscustomobject]@{
            Time = $time
            Outcome = $outcome
            ElapsedMs = $elapsed
            CandidateCount = $candidate
            DirectoryCount = $directories
            PathPrefix = $path
            Raw = $line
        })
    }

    return $events
}

function New-MarkdownReport {
    param(
        [object[]]$Rows,
        [object[]]$PrefilterEvents,
        [object[]]$MemoryBefore,
        [object[]]$MemoryAfter,
        [string]$PathPrefix,
        [string]$Backend,
        [string]$BuildResult,
        [string]$ReportJsonPath
    )

    $summary = $Rows | Group-Object Name | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Name = $_.Name
            Count = $items.Count
            AvgClientMs = [math]::Round(($items | Measure-Object ClientMs -Average).Average, 1)
            AvgHostMs = [math]::Round(($items | Measure-Object HostSearchMs -Average).Average, 1)
            AvgMatched = [math]::Round(($items | Measure-Object TotalMatchedCount -Average).Average, 1)
            AvgReturned = [math]::Round(($items | Measure-Object ReturnedCount -Average).Average, 1)
        }
    }

    $prefilterSummary = $PrefilterEvents | Group-Object Outcome | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Outcome = $_.Name
            Count = $items.Count
            AvgElapsedMs = [math]::Round(($items | Measure-Object ElapsedMs -Average).Average, 1)
            AvgCandidateCount = [math]::Round(($items | Measure-Object CandidateCount -Average).Average, 0)
            AvgDirectoryCount = [math]::Round(($items | Measure-Object DirectoryCount -Average).Average, 0)
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# MftScanner 基准测试报告")
    $lines.Add("")
    $lines.Add("- 测试时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("- 测试后端：$Backend")
    $lines.Add("- 路径前缀：``$PathPrefix``")
    $lines.Add("- 构建结果：$BuildResult")
    $lines.Add("- 原始 JSON：``$ReportJsonPath``")
    $lines.Add("")
    $lines.Add("## 查询汇总")
    $lines.Add("")
    $lines.Add("| 用例 | 次数 | 平均客户端耗时(ms) | 平均宿主耗时(ms) | 平均命中数 | 平均返回数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $summary) {
        $lines.Add("| $($item.Name) | $($item.Count) | $($item.AvgClientMs) | $($item.AvgHostMs) | $($item.AvgMatched) | $($item.AvgReturned) |")
    }

    $lines.Add("")
    $lines.Add("## 路径前置过滤")
    $lines.Add("")
    $lines.Add("| 结果 | 次数 | 平均耗时(ms) | 平均候选数 | 平均目录数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: |")
    foreach ($item in $prefilterSummary) {
        $outcome = Convert-OutcomeToChinese $item.Outcome
        $lines.Add("| $outcome | $($item.Count) | $($item.AvgElapsedMs) | $($item.AvgCandidateCount) | $($item.AvgDirectoryCount) |")
    }

    $lines.Add("")
    $lines.Add("## 查询明细")
    $lines.Add("")
    $lines.Add("| 用例 | 类型过滤 | 关键词 | 客户端耗时(ms) | 宿主耗时(ms) | 命中数 | 返回数 | 是否截断 |")
    $lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | --- |")
    foreach ($row in $Rows) {
        $keyword = ($row.Keyword -replace "\|", "\|")
        $truncated = Convert-BoolToChinese $row.IsTruncated
        $lines.Add("| $($row.Name) | $($row.Filter) | ``$keyword`` | $($row.ClientMs) | $($row.HostSearchMs) | $($row.TotalMatchedCount) | $($row.ReturnedCount) | $truncated |")
    }

    $lines.Add("")
    $lines.Add("## 测试后内存")
    $lines.Add("")
    $lines.Add("| 进程 | PID | 工作集(MB) | 私有内存(MB) | 路径 |")
    $lines.Add("| --- | ---: | ---: | ---: | --- |")
    foreach ($p in $MemoryAfter) {
        $path = if ([string]::IsNullOrWhiteSpace($p.Path)) { "" } else { $p.Path }
        $lines.Add("| $($p.ProcessName) | $($p.Id) | $($p.WorkingSetMB) | $($p.PrivateMB) | ``$path`` |")
    }

    return ($lines -join [Environment]::NewLine)
}

function Convert-OutcomeToChinese {
    param([string]$Outcome)

    switch ($Outcome) {
        "success" { return "成功" }
        "unresolved" { return "未解析，回退后置过滤" }
        default { return $Outcome }
    }
}

function Convert-BoolToChinese {
    param([object]$Value)

    if ($Value -eq $true) {
        return "是"
    }

    return "否"
}

$repoRoot = Resolve-RepoRoot
Set-Location $repoRoot

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $repoRoot $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$msbuild = Resolve-MSBuild
$project = Join-Path $repoRoot "PackageManager.csproj"
$hostExe = Join-Path $repoRoot "Assets\Tools\MftScanner.exe"
$coreDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\MftScanner.Core.dll"
$newtonsoftDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\Newtonsoft.Json.dll"
$logPath = Join-Path $env:LOCALAPPDATA "PackageManager\logs\index-service-diagnostics\$(Get-Date -Format 'yyyyMMdd').log"

Write-Host "Building MftScanner artifacts..."
& $msbuild $project /t:EnsureEmbeddedToolArtifacts "/p:Configuration=$Configuration" /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

if (!(Test-Path $hostExe)) {
    throw "Host executable not found: $hostExe"
}

$memoryBefore = @(Get-ProcessMemorySnapshot)
if ($Backend -eq "SharedHost" -and !$NoRestartHost) {
    Write-Host "Stopping existing MftScanner.exe processes..."
    Stop-MftScannerHost
    Start-Sleep -Milliseconds 500
}

$hostProcess = $null
if ($Backend -eq "SharedHost") {
    Write-Host "Starting index host..."
    $hostProcess = Start-Process -FilePath $hostExe -ArgumentList "--index-agent" -WindowStyle Hidden -PassThru
}

if (Test-Path $newtonsoftDll) {
    [void][System.Reflection.Assembly]::LoadFrom($newtonsoftDll)
}

[void][System.Reflection.Assembly]::LoadFrom($coreDll)

$client = if ($Backend -eq "SharedHost") {
    New-Object MftScanner.SharedIndexServiceClient "CtrlQBenchmark"
}
else {
    New-Object MftScanner.IndexService
}
$startedAt = Get-Date
$rows = New-Object System.Collections.Generic.List[object]

try {
    Write-Host "Waiting for shared index readiness..."
    $readyCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($ReadyTimeoutSeconds))
    $indexedCount = $client.BuildIndexAsync($null, $readyCts.Token).GetAwaiter().GetResult()
    $readyCts.Dispose()

    $cases = @(
        [pscustomobject]@{ Name = "PathContainsSingleChar"; Keyword = "$PathPrefix d"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathContainsTwoChars"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathWildcardExe"; Keyword = "$PathPrefix *.exe"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathLaunchableContains"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::Launchable },
        [pscustomobject]@{ Name = "GlobalLaunchableContains"; Keyword = "workbench"; Filter = [MftScanner.SearchTypeFilter]::Launchable }
    )

    foreach ($case in $cases) {
        for ($i = 1; $i -le $Repeat; $i++) {
            $searchCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $result = $client.SearchAsync($case.Keyword, $MaxResults, 0, $case.Filter, $null, $searchCts.Token).GetAwaiter().GetResult()
            $sw.Stop()
            $searchCts.Dispose()

            $rows.Add([pscustomobject]@{
                Name = $case.Name
                Iteration = $i
                Keyword = $case.Keyword
                Filter = $case.Filter.ToString()
                ClientMs = $sw.ElapsedMilliseconds
                HostSearchMs = $result.HostSearchMs
                TotalIndexedCount = $result.TotalIndexedCount
                TotalMatchedCount = $result.TotalMatchedCount
                ReturnedCount = @($result.Results).Count
                IsTruncated = $result.IsTruncated
                CharBucketReady = $result.ContainsBucketStatus.CharReady
                BigramBucketReady = $result.ContainsBucketStatus.BigramReady
                TrigramBucketReady = $result.ContainsBucketStatus.TrigramReady
            })
        }
    }
}
finally {
    if ($Backend -eq "SharedHost") {
        $client.Dispose()
    }
    else {
        $client.Shutdown()
    }
}

Start-Sleep -Milliseconds 300
$memoryAfter = @(Get-ProcessMemorySnapshot)
$prefilterEvents = @(Parse-PathPrefilterEvents -LogPath $logPath -Since $startedAt.AddSeconds(-1))

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $outputRoot "mft-benchmark-$timestamp.json"
$mdPath = Join-Path $outputRoot "mft-benchmark-$timestamp.md"

$report = [ordered]@{
    StartedAt = $startedAt
    PathPrefix = $PathPrefix
    Configuration = $Configuration
    MaxResults = $MaxResults
    Repeat = $Repeat
    Backend = $Backend
    HostProcessId = if ($hostProcess) { $hostProcess.Id } else { $null }
    LogPath = $logPath
    Rows = @($rows.ToArray())
    PathPrefilterEvents = @($prefilterEvents)
    MemoryBefore = @($memoryBefore)
    MemoryAfter = @($memoryAfter)
}

$report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
New-MarkdownReport `
    -Rows @($rows.ToArray()) `
    -PrefilterEvents @($prefilterEvents) `
    -MemoryBefore @($memoryBefore) `
    -MemoryAfter @($memoryAfter) `
    -PathPrefix $PathPrefix `
    -Backend $Backend `
    -BuildResult "EnsureEmbeddedToolArtifacts succeeded" `
    -ReportJsonPath $jsonPath |
    Set-Content -Path $mdPath -Encoding UTF8

Write-Host "Benchmark completed."
Write-Host "JSON: $jsonPath"
Write-Host "Markdown: $mdPath"
@($rows.ToArray()) | Format-Table Name, Filter, ClientMs, HostSearchMs, TotalMatchedCount, ReturnedCount, IsTruncated -AutoSize
@($prefilterEvents) | Format-Table Outcome, ElapsedMs, CandidateCount, DirectoryCount, PathPrefix -AutoSize
