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

function Parse-ContainsCacheEvents {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) {
        return @()
    }

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($match in Select-String -Path $LogPath -Pattern "\[CONTAINS CACHE\]" -ErrorAction SilentlyContinue) {
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
        $sourceCount = if ($line -match "sourceCount=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $matched = if ($line -match "matched=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $query = if ($line -match "query=(?<v>\S+)") { $Matches.v } else { "" }

        $events.Add([pscustomobject]@{
            Time = $time
            Outcome = $outcome
            ElapsedMs = $elapsed
            SourceCount = $sourceCount
            Matched = $matched
            Query = $query
            Raw = $line
        })
    }

    return $events
}

function Parse-ContainsQueryEvents {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) {
        return @()
    }

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($match in Select-String -Path $LogPath -Pattern "\[CONTAINS QUERY\]" -ErrorAction SilentlyContinue) {
        $line = $match.Line
        if ($line -notmatch "^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)") {
            continue
        }

        $time = [datetime]$Matches.time
        if ($time -lt $Since) {
            continue
        }

        $events.Add([pscustomobject]@{
            Time = $time
            Outcome = if ($line -match "outcome=(?<v>\S+)") { $Matches.v } else { "" }
            Mode = if ($line -match "mode=(?<v>\S+)") { $Matches.v } else { "" }
            Filter = if ($line -match "filter=(?<v>\S+)") { $Matches.v } else { "" }
            CandidateCount = if ($line -match "candidateCount=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            IntersectMs = if ($line -match "intersectMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            VerifyMs = if ($line -match "verifyMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            Matched = if ($line -match "matched=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            Normalized = if ($line -match "normalized=(?<v>\S+)") { $Matches.v } else { "" }
            Raw = $line
        })
    }

    return $events
}

function Parse-IndexStageEvents {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) {
        return @()
    }

    $patterns = "\[SNAPSHOT LOAD\]|\[SNAPSHOT RESTORE\]|\[SNAPSHOT RESTORE TOTAL\]|\[SNAPSHOT CATCHUP TOTAL\]|\[SNAPSHOT CATCHUP APPLY WAIT\]|\[MFT BUILD\]|\[CONTAINS WARMUP\]|\[CONTAINS SHORT HOT WARMUP\]|\[CONTAINS SHORT HOT BUILD\]"
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($match in Select-String -Path $LogPath -Pattern $patterns -ErrorAction SilentlyContinue) {
        $line = $match.Line
        if ($line -notmatch "^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)") {
            continue
        }

        $time = [datetime]$Matches.time
        if ($time -lt $Since) {
            continue
        }

        $stageMatches = [regex]::Matches($line, "\[(?<stage>[^\]]+)\]")
        $stage = if ($stageMatches.Count -gt 0) { $stageMatches[$stageMatches.Count - 1].Groups["stage"].Value } else { "" }
        $events.Add([pscustomobject]@{
            Time = $time
            Stage = $stage
            Outcome = if ($line -match "outcome=(?<v>\S+)") { $Matches.v } else { "" }
            ElapsedMs = if ($line -match "elapsedMs=(?<v>\d+)") { [int]$Matches.v } elseif ($line -match "totalMs=(?<v>\d+)") { [int]$Matches.v } elseif ($line -match "catchUpMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            LoadMs = if ($line -match "loadMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            RestoreMs = if ($line -match "restoreMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            ApplyMs = if ($line -match "applyMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            TotalChanges = if ($line -match "totalChanges=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            Stale = if ($line -match "stale=(?<v>true|false)") { [bool]::Parse($Matches.v) } else { $false }
            Raw = $line
        })
    }

    return $events
}

function Get-BadReturnedCount {
    param(
        [string]$Keyword,
        [string]$PathPrefix,
        [object]$Result
    )

    if ($null -eq $Result -or $null -eq $Result.Results) {
        return 0
    }

    $term = $Keyword
    if (![string]::IsNullOrWhiteSpace($PathPrefix) -and $Keyword.StartsWith($PathPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $term = $Keyword.Substring($PathPrefix.Length).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($term) -or $term.Contains("*") -or $term.Contains("?") -or $term.StartsWith("^") -or $term.EndsWith("$") -or ($term.StartsWith("/") -and $term.EndsWith("/"))) {
        return 0
    }

    return @($Result.Results | Where-Object {
        $_.FileName.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -lt 0
    }).Count
}

function New-MarkdownReport {
    param(
        [object[]]$Rows,
        [object[]]$PrefilterEvents,
        [object[]]$ContainsCacheEvents,
        [object[]]$ContainsQueryEvents,
        [object[]]$IndexStageEvents,
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
            AvgPhysicalMatched = [math]::Round(($items | Measure-Object PhysicalMatchedCount -Average).Average, 1)
            AvgUniqueMatched = [math]::Round(($items | Measure-Object UniqueMatchedCount -Average).Average, 1)
            AvgDuplicatePaths = [math]::Round(($items | Measure-Object DuplicatePathCount -Average).Average, 1)
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

    $cacheSummary = $ContainsCacheEvents | Group-Object Outcome | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Outcome = $_.Name
            Count = $items.Count
            AvgElapsedMs = [math]::Round(($items | Measure-Object ElapsedMs -Average).Average, 1)
            AvgSourceCount = [math]::Round(($items | Measure-Object SourceCount -Average).Average, 0)
            AvgMatched = [math]::Round(($items | Measure-Object Matched -Average).Average, 0)
        }
    }

    $containsSummary = $ContainsQueryEvents | Group-Object Mode | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Mode = $_.Name
            Count = $items.Count
            AvgCandidateCount = [math]::Round(($items | Measure-Object CandidateCount -Average).Average, 0)
            AvgIntersectMs = [math]::Round(($items | Measure-Object IntersectMs -Average).Average, 1)
            AvgVerifyMs = [math]::Round(($items | Measure-Object VerifyMs -Average).Average, 1)
            AvgMatched = [math]::Round(($items | Measure-Object Matched -Average).Average, 0)
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
    $lines.Add("| 用例 | 次数 | 平均客户端耗时(ms) | 平均宿主耗时(ms) | UI命中数 | 物理命中数 | 唯一路径数 | 重复路径数 | 平均返回数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $summary) {
        $lines.Add("| $($item.Name) | $($item.Count) | $($item.AvgClientMs) | $($item.AvgHostMs) | $($item.AvgMatched) | $($item.AvgPhysicalMatched) | $($item.AvgUniqueMatched) | $($item.AvgDuplicatePaths) | $($item.AvgReturned) |")
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
    $lines.Add("## Contains 增量缓存")
    $lines.Add("")
    $lines.Add("| 结果 | 次数 | 平均耗时(ms) | 平均输入候选数 | 平均命中数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: |")
    foreach ($item in $cacheSummary) {
        $outcome = Convert-CacheOutcomeToChinese $item.Outcome
        $lines.Add("| $outcome | $($item.Count) | $($item.AvgElapsedMs) | $($item.AvgSourceCount) | $($item.AvgMatched) |")
    }

    $lines.Add("")
    $lines.Add("## Contains 执行模式")
    $lines.Add("")
    $lines.Add("| 模式 | 次数 | 平均候选数 | 平均求交(ms) | 平均校验(ms) | 平均命中数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $containsSummary) {
        $lines.Add("| $($item.Mode) | $($item.Count) | $($item.AvgCandidateCount) | $($item.AvgIntersectMs) | $($item.AvgVerifyMs) | $($item.AvgMatched) |")
    }

    $lines.Add("")
    $lines.Add("## 索引加载与后台阶段")
    $lines.Add("")
    $lines.Add("| 时间 | 阶段 | 结果 | 耗时(ms) | load(ms) | restore(ms) | apply(ms) | 变更数 | stale |")
    $lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |")
    foreach ($item in $IndexStageEvents) {
        $lines.Add("| $($item.Time.ToString('HH:mm:ss.fff')) | $($item.Stage) | $($item.Outcome) | $($item.ElapsedMs) | $($item.LoadMs) | $($item.RestoreMs) | $($item.ApplyMs) | $($item.TotalChanges) | $(Convert-BoolToChinese $item.Stale) |")
    }

    $lines.Add("")
    $lines.Add("## 查询明细")
    $lines.Add("")
    $lines.Add("| 用例 | 类型过滤 | 关键词 | 客户端耗时(ms) | 宿主耗时(ms) | UI命中数 | 物理命中数 | 唯一路径数 | 重复路径数 | 返回数 | 错配返回 | stale | 是否截断 |")
    $lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |")
    foreach ($row in $Rows) {
        $keyword = ($row.Keyword -replace "\|", "\|")
        $truncated = Convert-BoolToChinese $row.IsTruncated
        $stale = Convert-BoolToChinese $row.IsSnapshotStale
        $lines.Add("| $($row.Name) | $($row.Filter) | ``$keyword`` | $($row.ClientMs) | $($row.HostSearchMs) | $($row.TotalMatchedCount) | $($row.PhysicalMatchedCount) | $($row.UniqueMatchedCount) | $($row.DuplicatePathCount) | $($row.ReturnedCount) | $($row.BadReturnedCount) | $stale | $truncated |")
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

function Convert-CacheOutcomeToChinese {
    param([string]$Outcome)

    switch ($Outcome) {
        "hit" { return "命中" }
        "miss" { return "未命中" }
        default { return $Outcome }
    }
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

$correctnessScript = Join-Path $repoRoot "scripts\Test-MftScannerSearchCorrectness.ps1"
if (Test-Path $correctnessScript) {
    Write-Host "Running correctness precheck..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $correctnessScript -Configuration $Configuration -Queries codex,code,c,vs -MaxResults 100 -SkipBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Correctness precheck failed with exit code $LASTEXITCODE."
    }
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
        [pscustomobject]@{ Name = "PathIncrementalV"; Keyword = "$PathPrefix v"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathIncrementalVe"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathIncrementalVer"; Keyword = "$PathPrefix ver"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathWildcardExe"; Keyword = "$PathPrefix *.exe"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "PathLaunchableContains"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::Launchable },
        [pscustomobject]@{ Name = "GlobalAllSingleChar"; Keyword = "d"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "GlobalAllTwoChars"; Keyword = "ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "GlobalIncrementalV"; Keyword = "v"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "GlobalIncrementalVe"; Keyword = "ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "GlobalIncrementalVer"; Keyword = "ver"; Filter = [MftScanner.SearchTypeFilter]::All },
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
                PhysicalMatchedCount = $result.PhysicalMatchedCount
                UniqueMatchedCount = $result.UniqueMatchedCount
                DuplicatePathCount = $result.DuplicatePathCount
                ReturnedCount = @($result.Results).Count
                BadReturnedCount = Get-BadReturnedCount -Keyword $case.Keyword -PathPrefix $PathPrefix -Result $result
                IsTruncated = $result.IsTruncated
                IsSnapshotStale = $result.IsSnapshotStale
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
$containsCacheEvents = @(Parse-ContainsCacheEvents -LogPath $logPath -Since $startedAt.AddSeconds(-1))
$containsQueryEvents = @(Parse-ContainsQueryEvents -LogPath $logPath -Since $startedAt.AddSeconds(-1))
$indexStageEvents = @(Parse-IndexStageEvents -LogPath $logPath -Since $startedAt.AddSeconds(-1))

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
    ContainsCacheEvents = @($containsCacheEvents)
    ContainsQueryEvents = @($containsQueryEvents)
    IndexStageEvents = @($indexStageEvents)
    MemoryBefore = @($memoryBefore)
    MemoryAfter = @($memoryAfter)
}

$report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
New-MarkdownReport `
    -Rows @($rows.ToArray()) `
    -PrefilterEvents @($prefilterEvents) `
    -ContainsCacheEvents @($containsCacheEvents) `
    -ContainsQueryEvents @($containsQueryEvents) `
    -IndexStageEvents @($indexStageEvents) `
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
@($containsCacheEvents) | Format-Table Outcome, ElapsedMs, SourceCount, Matched, Query -AutoSize
@($containsQueryEvents) | Format-Table Mode, CandidateCount, IntersectMs, VerifyMs, Matched, Normalized -AutoSize
@($indexStageEvents) | Format-Table Stage, Outcome, ElapsedMs, LoadMs, RestoreMs, ApplyMs, TotalChanges, Stale -AutoSize
