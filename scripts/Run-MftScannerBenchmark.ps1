param(
    [string]$Configuration = "Debug",
    [string]$PathPrefix = "$env:USERPROFILE\Desktop",
    [int]$MaxResults = 500,
    [int]$Repeat = 1,
    [int]$ReadyTimeoutSeconds = 180,
    [int]$SearchTimeoutSeconds = 60,
    [int]$MaxHostMsThreshold = 100,
    [int]$MaxRestoreReadyMsThreshold = 3000,
    [ValidateSet("SharedHost", "InProcess")]
    [string]$Backend = "SharedHost",
    [switch]$NoRestartHost,
    [switch]$SkipCorrectnessPrecheck,
    [switch]$ForceRebuildIndex,
    [string]$SnapshotDirectory,
    [string]$SharedHostConsumer = "Benchmark",
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

function Assert-IsolatedSnapshotDirectory {
    param(
        [string]$SnapshotRoot,
        [string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($SnapshotRoot)) {
        throw "ForceRebuildIndex requires -SnapshotDirectory. Refusing to touch the real MftScanner index."
    }

    $resolvedSnapshot = [System.IO.Path]::GetFullPath($SnapshotRoot)
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputRoot)
    $realLocal = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "PackageManager\MftScannerIndex"))
    $realRoaming = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA "PackageManager\MftScannerIndex"))

    if ($resolvedSnapshot.TrimEnd('\') -ieq $realLocal.TrimEnd('\') -or
        $resolvedSnapshot.TrimEnd('\') -ieq $realRoaming.TrimEnd('\')) {
        throw "SnapshotDirectory points at the real MftScanner index: $resolvedSnapshot"
    }

    if (!$resolvedSnapshot.StartsWith($resolvedOutput.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "SnapshotDirectory must be under OutputDirectory for destructive benchmark isolation. SnapshotDirectory=$resolvedSnapshot OutputDirectory=$resolvedOutput"
    }

    return $resolvedSnapshot
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
        $strategy = if ($line -match "strategy=(?<v>\S+)") { $Matches.v } else { "" }
        $elapsed = if ($line -match "elapsedMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $candidate = if ($line -match "candidateCount=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $directories = if ($line -match "directories=(?<v>\d+)") { [int]$Matches.v } else { 0 }
        $path = if ($line -match "pathPrefix=(?<v>.+)$") { $Matches.v } else { "" }

        $events.Add([pscustomobject]@{
            Time = $time
            Outcome = $outcome
            Strategy = $strategy
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

function Get-SearchTermForContains {
    param([string]$Keyword)

    if ([string]::IsNullOrWhiteSpace($Keyword)) {
        return ""
    }

    $trimmed = $Keyword.Trim()
    $lastSlash = [Math]::Max($trimmed.LastIndexOf("\"), $trimmed.LastIndexOf("/"))
    if ($lastSlash -ge 0) {
        $tail = $trimmed.Substring($lastSlash + 1).Trim()
        $space = $tail.LastIndexOf(" ")
        if ($space -ge 0 -and $space + 1 -lt $tail.Length) {
            return $tail.Substring($space + 1).Trim()
        }
    }

    $lastSpace = $trimmed.LastIndexOf(" ")
    if ($lastSpace -ge 1 -and $trimmed.Substring(0, $lastSpace).Contains(":")) {
        return $trimmed.Substring($lastSpace + 1).Trim()
    }

    return $trimmed
}

function Get-LatestContainsQueryEvent {
    param(
        [string]$LogPath,
        [datetime]$Since,
        [string]$Keyword
    )

    $term = Get-SearchTermForContains -Keyword $Keyword
    $events = @(Parse-ContainsQueryEvents -LogPath $LogPath -Since $Since)
    if ($events.Count -eq 0) {
        return $null
    }

    $matched = @($events | Where-Object {
        $_.Normalized -eq $term -or $_.Normalized -eq "'$term'" -or $_.Raw.Contains("normalized=$term") -or $_.Raw.Contains("normalized='$term'")
    })

    if ($matched.Count -gt 0) {
        return @($matched | Sort-Object Time | Select-Object -Last 1)[0]
    }

    return @($events | Sort-Object Time | Select-Object -Last 1)[0]
}

function Parse-IndexStageEvents {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) {
        return @()
    }

    $patterns = "\[V7 SNAPSHOT LOAD\]|\[V7 SNAPSHOT SAVE\]|\[SNAPSHOT LOAD\]|\[SNAPSHOT RESTORE\]|\[SNAPSHOT RESTORE TOTAL\]|\[SNAPSHOT CATCHUP TOTAL\]|\[SNAPSHOT CATCHUP APPLY WAIT\]|\[MFT BUILD\]|\[CONTAINS WARMUP\]|\[CONTAINS SHORT HOT WARMUP\]|\[CONTAINS SHORT HOT BUILD\]|\[DERIVED STRUCTURES\]|\[POSTINGS SNAPSHOT LOAD\]|\[POSTINGS SNAPSHOT RESTORE\]|\[POSTINGS SNAPSHOT SAVE\]|\[CONTAINS SNAPSHOT LOAD\]|\[NATIVE DERIVED BUILD\]|\[NATIVE BASE BUCKET BUILD\]|\[NATIVE MFT BUILD\]|\[NATIVE SNAPSHOT LOAD\]|\[NATIVE SNAPSHOT RESTORE\]|\[NATIVE SNAPSHOT RESTORE TOTAL\]|\[NATIVE V2 POSTINGS LOAD\]|\[NATIVE V2 POSTINGS SAVE\]|\[NATIVE V2 RUNTIME LOAD\]|\[NATIVE V2 RUNTIME SAVE\]|\[NATIVE V2 TYPE BUCKETS LOAD\]|\[NATIVE V2 TYPE BUCKETS SAVE\]"
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
            V2Ms = if ($line -match "v2Ms=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            RecordsMs = if ($line -match "recordsMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            DirsMs = if ($line -match "dirsMs=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            TotalChanges = if ($line -match "totalChanges=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            FileBytes = if ($line -match "fileBytes=(?<v>\d+)") { [long]$Matches.v } else { 0 }
            Records = if ($line -match "records=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            Buckets = if ($line -match "buckets=(?<v>\d+)") { [int]$Matches.v } else { 0 }
            Bytes = if ($line -match "bytes=(?<v>\d+)") { [long]$Matches.v } else { 0 }
            V2Bytes = if ($line -match "v2Bytes=(?<v>\d+)") { [long]$Matches.v } else { 0 }
            ShortBytes = if ($line -match "shortBytes=(?<v>\d+)") { [long]$Matches.v } else { 0 }
            ParentOrder = if ($line -match "parentOrder=(?<v>\d+)") { [long]$Matches.v } else { 0 }
            ChildBuckets = if ($line -match "childBuckets=(?<v>\d+)") { [long]$Matches.v } else { 0 }
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
        [string]$ReportJsonPath,
        [int]$MaxHostMsThreshold,
        [int]$MaxRestoreReadyMsThreshold,
        [long]$RestoreReadyMs,
        [object[]]$Failures
    )

    $summary = $Rows | Group-Object Phase, Name | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Phase = $items[0].Phase
            Name = $items[0].Name
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

    $prefilterSummary = $PrefilterEvents | Group-Object Strategy | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Strategy = $_.Name
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

    $perceptionSummary = $Rows | Where-Object { $_.Scenario -eq "Typing" -or $_.Language -eq "Chinese" } | Group-Object Phase, Scenario | ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            Phase = $items[0].Phase
            Scenario = $items[0].Scenario
            Count = $items.Count
            MaxClientMs = ($items | Measure-Object ClientMs -Maximum).Maximum
            MaxHostMs = ($items | Measure-Object HostSearchMs -Maximum).Maximum
            AvgHostMs = [math]::Round(($items | Measure-Object HostSearchMs -Average).Average, 1)
            FallbackRows = @($items | Where-Object { $_.ContainsMode -eq "fallback" }).Count
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# MftScanner 基准测试报告")
    $lines.Add("")
    $passed = @($Failures).Count -eq 0
    $resultText = if ($passed) { "通过" } else { "不通过" }
    $lines.Add("## 结论")
    $lines.Add("")
    $lines.Add("- 结果：$resultText")
    $lines.Add("- 搜索宿主耗时阈值：$MaxHostMsThreshold ms")
    $lines.Add("- 启动恢复 ready 阈值：$MaxRestoreReadyMsThreshold ms")
    $lines.Add("- 当前恢复 ready 耗时：$RestoreReadyMs ms")
    if (-not $passed) {
        $lines.Add("")
        $lines.Add("| 阶段 | 用例 | 关键词 | 类型过滤 | 客户端(ms) | 宿主(ms) | Contains模式 | 命中 | 阈值 | 原因 |")
        $lines.Add("| --- | --- | --- | --- | ---: | ---: | --- | ---: | ---: | --- |")
        foreach ($failure in @($Failures)) {
            $keyword = if ($failure.Keyword) { $failure.Keyword.ToString().Replace("|", "/") } else { "" }
            $lines.Add("| $($failure.Phase) | $($failure.Name) | ``$keyword`` | $($failure.Filter) | $($failure.ClientMs) | $($failure.HostSearchMs) | $($failure.ContainsMode) | $($failure.TotalMatchedCount) | $($failure.ThresholdMs) | $($failure.Reason) |")
        }
    }
    $lines.Add("")
    $lines.Add("- 测试时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("- 测试后端：$Backend")
    $lines.Add("- 路径前缀：``$PathPrefix``")
    $lines.Add("- 构建结果：$BuildResult")
    $lines.Add("- 原始 JSON：``$ReportJsonPath``")
    $lines.Add("")
    $lines.Add("## 查询汇总")
    $lines.Add("")
    $lines.Add("| 阶段 | 用例 | 次数 | 平均客户端耗时(ms) | 平均宿主耗时(ms) | UI命中数 | 物理命中数 | 唯一路径数 | 重复路径数 | 平均返回数 |")
    $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $summary) {
        $lines.Add("| $($item.Phase) | $($item.Name) | $($item.Count) | $($item.AvgClientMs) | $($item.AvgHostMs) | $($item.AvgMatched) | $($item.AvgPhysicalMatched) | $($item.AvgUniqueMatched) | $($item.AvgDuplicatePaths) | $($item.AvgReturned) |")
    }

    $lines.Add("")
    $lines.Add("## 真实体感覆盖")
    $lines.Add("")
    $lines.Add("| 阶段 | 场景 | 次数 | 最大客户端(ms) | 最大宿主(ms) | 平均宿主(ms) | fallback行数 |")
    $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $perceptionSummary) {
        $lines.Add("| $($item.Phase) | $($item.Scenario) | $($item.Count) | $($item.MaxClientMs) | $($item.MaxHostMs) | $($item.AvgHostMs) | $($item.FallbackRows) |")
    }

    $lines.Add("")
    $lines.Add("## 路径前置过滤")
    $lines.Add("")
    $lines.Add("| 策略 | 次数 | 平均耗时(ms) | 平均候选数 | 平均目录数 |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: |")
    foreach ($item in $prefilterSummary) {
        $strategy = if ([string]::IsNullOrWhiteSpace($item.Strategy)) { "未记录" } else { $item.Strategy }
        $lines.Add("| $strategy | $($item.Count) | $($item.AvgElapsedMs) | $($item.AvgCandidateCount) | $($item.AvgDirectoryCount) |")
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
    $lines.Add("| 时间 | 阶段 | 结果 | 耗时(ms) | load(ms) | restore(ms) | v2(ms) | records(ms) | dirs(ms) | apply(ms) | 记录数 | 文件(MB) | buckets | bytes(MB) | v2(MB) | short(MB) | parentOrder | childBuckets | 变更数 | stale |")
    $lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")
    foreach ($item in $IndexStageEvents) {
        $fileMb = if ($item.FileBytes -gt 0) { [math]::Round($item.FileBytes / 1MB, 1) } else { 0 }
        $bytesMb = if ($item.Bytes -gt 0) { [math]::Round($item.Bytes / 1MB, 1) } else { 0 }
        $v2Mb = if ($item.V2Bytes -gt 0) { [math]::Round($item.V2Bytes / 1MB, 1) } else { 0 }
        $shortMb = if ($item.ShortBytes -gt 0) { [math]::Round($item.ShortBytes / 1MB, 1) } else { 0 }
        $lines.Add("| $($item.Time.ToString('HH:mm:ss.fff')) | $($item.Stage) | $($item.Outcome) | $($item.ElapsedMs) | $($item.LoadMs) | $($item.RestoreMs) | $($item.V2Ms) | $($item.RecordsMs) | $($item.DirsMs) | $($item.ApplyMs) | $($item.Records) | $fileMb | $($item.Buckets) | $bytesMb | $v2Mb | $shortMb | $($item.ParentOrder) | $($item.ChildBuckets) | $($item.TotalChanges) | $(Convert-BoolToChinese $item.Stale) |")
    }

    $lines.Add("")
    $lines.Add("## 查询明细")
    $lines.Add("")
    $lines.Add("| 阶段 | 场景 | 用例 | 类型过滤 | 关键词 | 客户端耗时(ms) | 宿主耗时(ms) | Contains模式 | UI命中数 | 物理命中数 | 唯一路径数 | 重复路径数 | 返回数 | 错配返回 | stale | 桶状态 | 是否截断 |")
    $lines.Add("| --- | --- | --- | --- | --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |")
    foreach ($row in $Rows) {
        $keyword = ($row.Keyword -replace "\|", "\|")
        $truncated = Convert-BoolToChinese $row.IsTruncated
        $stale = Convert-BoolToChinese $row.IsSnapshotStale
        $bucket = "C=$($row.CharBucketReady),B=$($row.BigramBucketReady),T=$($row.TrigramBucketReady)"
        $lines.Add("| $($row.Phase) | $($row.Scenario) | $($row.Name) | $($row.Filter) | ``$keyword`` | $($row.ClientMs) | $($row.HostSearchMs) | $($row.ContainsMode) | $($row.TotalMatchedCount) | $($row.PhysicalMatchedCount) | $($row.UniqueMatchedCount) | $($row.DuplicatePathCount) | $($row.ReturnedCount) | $($row.BadReturnedCount) | $stale | $bucket | $truncated |")
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

$resolvedSnapshotDirectory = $null
if (![string]::IsNullOrWhiteSpace($SnapshotDirectory)) {
    $resolvedSnapshotDirectory = [System.IO.Path]::GetFullPath($SnapshotDirectory)
    New-Item -ItemType Directory -Force -Path $resolvedSnapshotDirectory | Out-Null
    $env:PM_MFT_INDEX_SNAPSHOT_DIR = $resolvedSnapshotDirectory
    Write-Host "Using isolated snapshot directory: $resolvedSnapshotDirectory"
}

if ($ForceRebuildIndex) {
    $resolvedSnapshotDirectory = Assert-IsolatedSnapshotDirectory -SnapshotRoot $resolvedSnapshotDirectory -OutputRoot $outputRoot
    $env:PM_MFT_INDEX_SNAPSHOT_DIR = $resolvedSnapshotDirectory
}

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

$snapshotBackup = $null
if ($ForceRebuildIndex) {
    Write-Host "Force rebuild requested with isolated snapshot directory; stopping host without touching the real index..."
    Stop-MftScannerHost
}
else {
    $correctnessScript = Join-Path $repoRoot "scripts\Test-MftScannerSearchCorrectness.ps1"
    if (!$SkipCorrectnessPrecheck -and (Test-Path $correctnessScript)) {
        Write-Host "Running correctness precheck..."
        & powershell -NoProfile -ExecutionPolicy Bypass -File $correctnessScript -Configuration $Configuration -Queries codex,code,c,vs,d,ve,dx,qx,zz,_d,1x,ver -MaxResults 100 -SkipBuild
        if ($LASTEXITCODE -ne 0) {
            throw "Correctness precheck failed with exit code $LASTEXITCODE."
        }
    }
    elseif ($SkipCorrectnessPrecheck) {
        Write-Host "Skipping correctness precheck."
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
    New-Object MftScanner.SharedIndexServiceClient $SharedHostConsumer
}
else {
    New-Object MftScanner.IndexService
}
$startedAt = Get-Date
$rows = New-Object System.Collections.Generic.List[object]
$dynamicChineseTerms = New-Object System.Collections.Generic.List[string]

function Add-DynamicChineseTerm {
    param([string]$Value)

    if (![string]::IsNullOrWhiteSpace($Value) -and -not $dynamicChineseTerms.Contains($Value)) {
        $dynamicChineseTerms.Add($Value)
    }
}

function Add-DynamicChineseTermsFromResult {
    param([object]$Result)

    foreach ($item in @($Result.Results)) {
        $name = $item.FileName
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        for ($i = 0; $i -lt $name.Length; $i++) {
            $maxLength = [Math]::Min(4, $name.Length - $i)
            for ($length = 1; $length -le $maxLength; $length++) {
                $allChinese = $true
                for ($j = 0; $j -lt $length; $j++) {
                    $code = [int][char]$name[$i + $j]
                    if ($code -lt 0x4E00 -or $code -gt 0x9FFF) {
                        $allChinese = $false
                        break
                    }
                }

                if ($allChinese) {
                    Add-DynamicChineseTerm $name.Substring($i, $length)
                }
            }

            if ($dynamicChineseTerms.Count -ge 12) {
                return
            }
        }
    }
}

function Invoke-BenchmarkCase {
    param(
        [object]$Client,
        [object]$Case,
        [string]$Phase,
        [int]$Iteration
    )

    $searchStartedAt = Get-Date
    $searchCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = $Client.SearchAsync($Case.Keyword, $MaxResults, 0, $Case.Filter, $null, $searchCts.Token).GetAwaiter().GetResult()
        $sw.Stop()
        $event = Get-LatestContainsQueryEvent -LogPath $logPath -Since $searchStartedAt.AddMilliseconds(-100) -Keyword $Case.Keyword
        $rows.Add([pscustomobject]@{
            Phase = $Phase
            Scenario = $Case.Scenario
            Language = $Case.Language
            Name = $Case.Name
            Iteration = $Iteration
            Keyword = $Case.Keyword
            Filter = $Case.Filter.ToString()
            ClientMs = $sw.ElapsedMilliseconds
            HostSearchMs = $result.HostSearchMs
            ContainsMode = if ($event) { $event.Mode } else { "" }
            ContainsCandidateCount = if ($event) { $event.CandidateCount } else { 0 }
            ContainsVerifyMs = if ($event) { $event.VerifyMs } else { 0 }
            TotalIndexedCount = $result.TotalIndexedCount
            TotalMatchedCount = $result.TotalMatchedCount
            PhysicalMatchedCount = $result.PhysicalMatchedCount
            UniqueMatchedCount = $result.UniqueMatchedCount
            DuplicatePathCount = $result.DuplicatePathCount
            ReturnedCount = @($result.Results).Count
            BadReturnedCount = Get-BadReturnedCount -Keyword $Case.Keyword -PathPrefix $PathPrefix -Result $result
            IsTruncated = $result.IsTruncated
            IsSnapshotStale = $result.IsSnapshotStale
            CharBucketReady = $result.ContainsBucketStatus.CharReady
            BigramBucketReady = $result.ContainsBucketStatus.BigramReady
            TrigramBucketReady = $result.ContainsBucketStatus.TrigramReady
        })

        if ($Case.Language -like "Chinese*") {
            Add-DynamicChineseTermsFromResult -Result $result
        }
    }
    finally {
        $searchCts.Dispose()
    }
}

try {
    Write-Host "Waiting for shared index readiness..."
    $readyCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($ReadyTimeoutSeconds))
    $indexedCount = $client.BuildIndexAsync($null, $readyCts.Token).GetAwaiter().GetResult()
    $readyCts.Dispose()

    $cases = @(
        [pscustomobject]@{ Name = "PathContainsSingleChar"; Keyword = "$PathPrefix d"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathContainsTwoChars"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathSparseDx"; Keyword = "$PathPrefix dx"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathSparseQx"; Keyword = "$PathPrefix qx"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathSparseZz"; Keyword = "$PathPrefix zz"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathSparseUnderscoreD"; Keyword = "$PathPrefix _d"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathSparse1x"; Keyword = "$PathPrefix 1x"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathIncrementalV"; Keyword = "$PathPrefix v"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathIncrementalVe"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathIncrementalVer"; Keyword = "$PathPrefix ver"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathWildcardExe"; Keyword = "$PathPrefix *.exe"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathLaunchableContains"; Keyword = "$PathPrefix ve"; Filter = [MftScanner.SearchTypeFilter]::Launchable; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "PathConfigCalsupport"; Keyword = "$PathPrefix calsupport"; Filter = [MftScanner.SearchTypeFilter]::Config; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalAllSingleChar"; Keyword = "d"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalAllTwoChars"; Keyword = "ve"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalSparseDx"; Keyword = "dx"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalSparseQx"; Keyword = "qx"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalSparseZz"; Keyword = "zz"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalSparseUnderscoreD"; Keyword = "_d"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalSparse1x"; Keyword = "1x"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalIncrementalV"; Keyword = "v"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalIncrementalVe"; Keyword = "ve"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalIncrementalVer"; Keyword = "ver"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalChineseSingle"; Keyword = "我"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Chinese" },
        [pscustomobject]@{ Name = "GlobalChineseBigram"; Keyword = "鱼丸"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Direct"; Language = "Chinese" },
        [pscustomobject]@{ Name = "GlobalChineseSearch"; Keyword = "搜索"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Chinese" },
        [pscustomobject]@{ Name = "GlobalChineseTypingSou"; Keyword = "搜"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Typing"; Language = "Chinese" },
        [pscustomobject]@{ Name = "GlobalConfigCalsupport"; Keyword = "calsupport"; Filter = [MftScanner.SearchTypeFilter]::Config; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "GlobalLaunchableContains"; Keyword = "workbench"; Filter = [MftScanner.SearchTypeFilter]::Launchable; Scenario = "Direct"; Language = "Ascii" },
        [pscustomobject]@{ Name = "ChineseSeed"; Keyword = "中"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Seed"; Language = "ChineseSeed" }
    )

    if ($Backend -eq "SharedHost") {
        Write-Host "Warming shared-host IPC path..."
        $warmupCase = [pscustomobject]@{ Name = "WarmupPathContainsSingleChar"; Keyword = "$PathPrefix d"; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Warmup"; Language = "Ascii" }
        $warmupCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
        try {
            [void]$client.SearchAsync($warmupCase.Keyword, $MaxResults, 0, $warmupCase.Filter, $null, $warmupCts.Token).GetAwaiter().GetResult()
        }
        finally {
            $warmupCts.Dispose()
        }
    }

    foreach ($case in $cases) {
        for ($i = 1; $i -le $Repeat; $i++) {
            Invoke-BenchmarkCase -Client $client -Case $case -Phase "just-built" -Iteration $i
        }
    }

    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    do {
        $state = if ($client.ContainsBucketStatus) { $client.ContainsBucketStatus } else { $null }
        if ($state -and $state.CharReady -and $state.BigramReady -and $state.TrigramReady) {
            break
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    $dynamicCases = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $dynamicChineseTerms.Count; $i++) {
        $term = $dynamicChineseTerms[$i]
        $dynamicCases.Add([pscustomobject]@{ Name = "DynamicChinese$($i + 1)"; Keyword = $term; Filter = [MftScanner.SearchTypeFilter]::All; Scenario = "Dynamic"; Language = "Chinese" })
    }

    foreach ($case in @($cases + $dynamicCases.ToArray())) {
        for ($i = 1; $i -le $Repeat; $i++) {
            Invoke-BenchmarkCase -Client $client -Case $case -Phase "postings-ready" -Iteration $i
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
$restoreReadyMs = 0
$restoreTotal = @($indexStageEvents | Where-Object { $_.Stage -eq "SNAPSHOT RESTORE TOTAL" } | Sort-Object Time | Select-Object -Last 1)
if ($restoreTotal.Count -gt 0) {
    $restoreReadyMs = [int64]$restoreTotal[0].ElapsedMs
}

$failures = New-Object System.Collections.Generic.List[object]
if ($restoreReadyMs -gt $MaxRestoreReadyMsThreshold -or $restoreReadyMs -le 0) {
    $failures.Add([pscustomobject]@{
        Phase = "restore"
        Name = "SnapshotRestoreReady"
        Keyword = ""
        Filter = ""
        ClientMs = 0
        HostSearchMs = $restoreReadyMs
        ContainsMode = ""
        TotalMatchedCount = 0
        ThresholdMs = $MaxRestoreReadyMsThreshold
        Reason = "restore-ready-timeout"
    })
}
foreach ($row in @($rows.ToArray())) {
    if ($row.HostSearchMs -gt $MaxHostMsThreshold -or $row.HostSearchMs -lt 0) {
        $failures.Add([pscustomobject]@{
            Phase = $row.Phase
            Name = $row.Name
            Keyword = $row.Keyword
            Filter = $row.Filter
            ClientMs = $row.ClientMs
            HostSearchMs = $row.HostSearchMs
            ContainsMode = $row.ContainsMode
            TotalMatchedCount = $row.TotalMatchedCount
            ThresholdMs = $MaxHostMsThreshold
            Reason = "host-search-threshold"
        })
    }
}

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
    ForceRebuildIndex = [bool]$ForceRebuildIndex
    SnapshotDirectory = $resolvedSnapshotDirectory
    SnapshotBackup = $snapshotBackup
    HostProcessId = if ($hostProcess) { $hostProcess.Id } else { $null }
    LogPath = $logPath
    Rows = @($rows.ToArray())
    PathPrefilterEvents = @($prefilterEvents)
    ContainsCacheEvents = @($containsCacheEvents)
    ContainsQueryEvents = @($containsQueryEvents)
    IndexStageEvents = @($indexStageEvents)
    MemoryBefore = @($memoryBefore)
    MemoryAfter = @($memoryAfter)
    RestoreReadyMs = $restoreReadyMs
    MaxHostMsThreshold = $MaxHostMsThreshold
    MaxRestoreReadyMsThreshold = $MaxRestoreReadyMsThreshold
    Failures = @($failures.ToArray())
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
    -ReportJsonPath $jsonPath `
    -MaxHostMsThreshold $MaxHostMsThreshold `
    -MaxRestoreReadyMsThreshold $MaxRestoreReadyMsThreshold `
    -RestoreReadyMs $restoreReadyMs `
    -Failures @($failures.ToArray()) |
    Set-Content -Path $mdPath -Encoding UTF8

Write-Host "Benchmark completed."
Write-Host "JSON: $jsonPath"
Write-Host "Markdown: $mdPath"
@($rows.ToArray()) | Format-Table Name, Filter, ClientMs, HostSearchMs, TotalMatchedCount, ReturnedCount, IsTruncated -AutoSize
@($prefilterEvents) | Format-Table Outcome, ElapsedMs, CandidateCount, DirectoryCount, PathPrefix -AutoSize
@($containsCacheEvents) | Format-Table Outcome, ElapsedMs, SourceCount, Matched, Query -AutoSize
@($containsQueryEvents) | Format-Table Mode, CandidateCount, IntersectMs, VerifyMs, Matched, Normalized -AutoSize
@($indexStageEvents) | Format-Table Stage, Outcome, ElapsedMs, LoadMs, RestoreMs, V2Ms, RecordsMs, DirsMs, ApplyMs, TotalChanges, Stale -AutoSize
