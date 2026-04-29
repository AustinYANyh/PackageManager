param(
    [string]$Configuration = "Debug",
    [string]$OutputDirectory = ".\artifacts\mft-full-validation",
    [int]$ReadyTimeoutSeconds = 180,
    [int]$WarmupTimeoutSeconds = 70,
    [int]$ShortWarmupDelaySeconds = 4,
    [int]$SearchTimeoutSeconds = 90,
    [int]$MaxResults = 500,
    [switch]$NoBuild,
    [switch]$NoOpenReport
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
}

function Resolve-MSBuild {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "MSBuild.exe was not found."
}

function Stop-MftScannerHost {
    $stopped = 0
    $accessDenied = 0
    Get-CimInstance Win32_Process -Filter "Name = 'MftScanner.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                $stopped++
            }
            catch {
                $accessDenied++
                Write-Warning "Cannot stop MftScanner.exe pid=$($_.ProcessId): $($_.Exception.Message)"
            }
        }

    [pscustomobject]@{ Stopped = $stopped; AccessDenied = $accessDenied }
}

function Get-MftProcessMemory {
    Get-Process | Where-Object { $_.ProcessName -match 'MftScanner|PackageManager' } |
        Select-Object Id, ProcessName, Path,
            @{ Name = "WorkingSetMB"; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
            @{ Name = "PrivateMB"; Expression = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } }
}

function Read-SharedState {
    try {
        $map = [MftScanner.SharedIndexMemoryProtocol]::OpenStateMapForRead()
        try {
            return [MftScanner.SharedIndexMemoryProtocol]::ReadState($map)
        }
        finally {
            $map.Dispose()
        }
    }
    catch {
        return $null
    }
}

function Wait-SharedHostWarmup {
    param([int]$TimeoutSeconds)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $last = Read-SharedState
        if ($last -and $last.ContainsBucketStatus -and $last.ContainsBucketStatus.TrigramReady) {
            $sw.Stop()
            return [pscustomobject]@{
                Ready = $true
                WaitMs = $sw.ElapsedMilliseconds
                Status = $last.StatusMessage
                TrigramReady = $true
                CharReady = $last.ContainsBucketStatus.CharReady
                BigramReady = $last.ContainsBucketStatus.BigramReady
            }
        }

        Start-Sleep -Milliseconds 500
    }

    $sw.Stop()
    [pscustomobject]@{
        Ready = $false
        WaitMs = $sw.ElapsedMilliseconds
        Status = if ($last) { $last.StatusMessage } else { "" }
        TrigramReady = $false
        CharReady = if ($last -and $last.ContainsBucketStatus) { $last.ContainsBucketStatus.CharReady } else { $false }
        BigramReady = if ($last -and $last.ContainsBucketStatus) { $last.ContainsBucketStatus.BigramReady } else { $false }
    }
}

function Invoke-SearchCase {
    param(
        [object]$Client,
        [object]$Case,
        [string]$Phase
    )

    $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = $Client.SearchAsync($Case.Keyword, $MaxResults, 0, $Case.Filter, $null, $cts.Token).GetAwaiter().GetResult()
        $sw.Stop()
        [pscustomobject]@{
            Phase = $Phase
            Name = $Case.Name
            Keyword = $Case.Keyword
            Filter = $Case.Filter.ToString()
            ClientMs = $sw.ElapsedMilliseconds
            HostMs = $result.HostSearchMs
            Matched = $result.TotalMatchedCount
            Physical = $result.PhysicalMatchedCount
            Unique = $result.UniqueMatchedCount
            Returned = @($result.Results).Count
            Truncated = $result.IsTruncated
            Stale = $result.IsSnapshotStale
            TrigramReady = $result.ContainsBucketStatus.TrigramReady
            CharReady = $result.ContainsBucketStatus.CharReady
            BigramReady = $result.ContainsBucketStatus.BigramReady
            Error = ""
        }
    }
    catch {
        $sw.Stop()
        [pscustomobject]@{
            Phase = $Phase
            Name = $Case.Name
            Keyword = $Case.Keyword
            Filter = $Case.Filter.ToString()
            ClientMs = $sw.ElapsedMilliseconds
            HostMs = -1
            Matched = -1
            Physical = -1
            Unique = -1
            Returned = 0
            Truncated = $false
            Stale = $false
            TrigramReady = $false
            CharReady = $false
            BigramReady = $false
            Error = $_.Exception.Message
        }
    }
    finally {
        $cts.Dispose()
    }
}

function Get-RecentLogLines {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) { return @() }
    $patterns = "\[SNAPSHOT RESTORE TOTAL\]|\[SNAPSHOT LOAD\]|\[POSTINGS SNAPSHOT RESTORE\]|\[CONTAINS WARMUP\]|\[PATH PREFILTER\]|\[CONTAINS QUERY\]|\[MMF\]|\[MMF HOST\]"
    Select-String -Path $LogPath -Pattern $patterns -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Line -match "^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)" -and ([datetime]$Matches.time) -ge $Since
        } |
        Select-Object -ExpandProperty Line
}

function Write-MarkdownReport {
    param(
        [string]$Path,
        [object]$Summary,
        [object[]]$Rows,
        [object[]]$MemoryBefore,
        [object[]]$MemoryAfter,
        [string[]]$LogLines,
        [string]$JsonPath
    )

    $slow = @($Rows | Where-Object { $_.Error -or $_.ClientMs -gt 60 -or $_.HostMs -gt 60 } | Sort-Object @{ Expression = "ClientMs"; Descending = $true } | Select-Object -First 30)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# MftScanner 一键全功能验证报告")
    $lines.Add("")
    $lines.Add("- 时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("- 原始 JSON：``$JsonPath``")
    $lines.Add("- Indexed：$($Summary.IndexedCount)")
    $lines.Add("- Build/Ready：$($Summary.ReadyMs) ms")
    $lines.Add("- Warmup：$($Summary.WarmupMs) ms，TrigramReady=$($Summary.TrigramReady)")
    $lines.Add("- ShortWarmupDelay：$($Summary.ShortWarmupDelayMs) ms")
    $lines.Add("- HostReuse：$($Summary.HostReuse)")
    $lines.Add("- OldHost：$($Summary.OldHost)")
    $lines.Add("- HostProcessId：$($Summary.HostProcessId)")
    $lines.Add("")

    $lines.Add("## 慢查询 Top")
    $lines.Add("")
    $lines.Add("| 阶段 | 用例 | 类型 | 客户端(ms) | 宿主(ms) | 命中 | 返回 | Trigram | 错误 |")
    $lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | --- | --- |")
    foreach ($row in $slow) {
        $err = if ($null -ne $row.Error) { $row.Error.ToString().Replace("|", "/") } else { "" }
        $lines.Add("| $($row.Phase) | $($row.Name) | $($row.Filter) | $($row.ClientMs) | $($row.HostMs) | $($row.Matched) | $($row.Returned) | $($row.TrigramReady) | $err |")
    }

    $lines.Add("")
    $lines.Add("## 查询明细")
    $lines.Add("")
    $lines.Add("| 阶段 | 用例 | 关键词 | 类型 | 客户端(ms) | 宿主(ms) | 命中 | 物理 | 唯一 | 返回 | Trigram |")
    $lines.Add("| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |")
    foreach ($row in $Rows) {
        $keyword = if ($null -ne $row.Keyword) { $row.Keyword.ToString().Replace("|", "/") } else { "" }
        $lines.Add("| $($row.Phase) | $($row.Name) | ``$keyword`` | $($row.Filter) | $($row.ClientMs) | $($row.HostMs) | $($row.Matched) | $($row.Physical) | $($row.Unique) | $($row.Returned) | $($row.TrigramReady) |")
    }

    $lines.Add("")
    $lines.Add("## 内存")
    $lines.Add("")
    $lines.Add("| 阶段 | PID | 进程 | Private(MB) | WorkingSet(MB) |")
    $lines.Add("| --- | ---: | --- | ---: | ---: |")
    foreach ($m in $MemoryBefore) {
        $lines.Add("| before | $($m.Id) | $($m.ProcessName) | $($m.PrivateMB) | $($m.WorkingSetMB) |")
    }
    foreach ($m in $MemoryAfter) {
        $lines.Add("| after | $($m.Id) | $($m.ProcessName) | $($m.PrivateMB) | $($m.WorkingSetMB) |")
    }

    $lines.Add("")
    $lines.Add("## 关键日志")
    $lines.Add("")
    $lines.Add('```text')
    foreach ($line in ($LogLines | Select-Object -Last 160)) {
        $lines.Add($line)
    }
    $lines.Add('```')
    Set-Content -Path $Path -Value $lines -Encoding UTF8
}

$repoRoot = Resolve-RepoRoot
Set-Location $repoRoot
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$startedAt = Get-Date
$logDate = Get-Date -Format "yyyyMMdd"
$logPath = Join-Path $env:LOCALAPPDATA ("PackageManager\logs\index-service-diagnostics\{0}.log" -f $logDate)
$hostExe = Join-Path $repoRoot "Assets\Tools\MftScanner.exe"
$coreDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\MftScanner.Core.dll"
$newtonsoftDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\Newtonsoft.Json.dll"
$buildResult = "Skipped"
$failure = $null
$client = $null
$hostProcess = $null

try {
    if (!$NoBuild) {
        $msbuild = Resolve-MSBuild
        & $msbuild (Join-Path $repoRoot "PackageManager.csproj") /t:EnsureEmbeddedToolArtifacts "/p:Configuration=$Configuration" /v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
        $buildResult = "Succeeded"
    }

    $correctnessScript = Join-Path $repoRoot "scripts\Test-MftScannerSearchCorrectness.ps1"
    if (Test-Path $correctnessScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $correctnessScript -Configuration $Configuration -Queries "codex,code,c,vs,calsupport,workbench" -MaxResults 100 -SkipBuild
        if ($LASTEXITCODE -ne 0) { throw "Correctness precheck failed with exit code $LASTEXITCODE." }
    }

    if (Test-Path $newtonsoftDll) { [void][System.Reflection.Assembly]::LoadFrom($newtonsoftDll) }
    [void][System.Reflection.Assembly]::LoadFrom($coreDll)

    $memoryBefore = @(Get-MftProcessMemory)
    $stopResult = Stop-MftScannerHost
    Start-Sleep -Milliseconds 500
    $hostReuse = $false
    if ($stopResult.AccessDenied -gt 0) {
        $hostReuse = $true
    }
    else {
        $hostProcess = Start-Process -FilePath $hostExe -ArgumentList "--index-agent" -WindowStyle Hidden -PassThru
    }

    $client = New-Object MftScanner.SharedIndexServiceClient "CtrlQFullValidation"
    $readyCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($ReadyTimeoutSeconds))
    $readySw = [System.Diagnostics.Stopwatch]::StartNew()
    $indexedCount = $client.BuildIndexAsync($null, $readyCts.Token).GetAwaiter().GetResult()
    $readySw.Stop()
    $readyCts.Dispose()

    $desktop = [Environment]::GetFolderPath("Desktop")
    $cases = @(
        [pscustomobject]@{ Name = "Global 1char d"; Keyword = "d"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Global 2char ve"; Keyword = "ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Global 3char ver"; Keyword = "ver"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Global codex"; Keyword = "codex"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Global calsupport Config"; Keyword = "calsupport"; Filter = [MftScanner.SearchTypeFilter]::Config },
        [pscustomobject]@{ Name = "Global workbench Launchable"; Keyword = "workbench"; Filter = [MftScanner.SearchTypeFilter]::Launchable },
        [pscustomobject]@{ Name = "Global *.log"; Keyword = "*.log"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Global log Log"; Keyword = "log"; Filter = [MftScanner.SearchTypeFilter]::Log },
        [pscustomobject]@{ Name = "Desktop d"; Keyword = "$desktop d"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Desktop ve"; Keyword = "$desktop ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Desktop ver"; Keyword = "$desktop ver"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Desktop *.exe"; Keyword = "$desktop *.exe"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Desktop ve Launchable"; Keyword = "$desktop ve"; Filter = [MftScanner.SearchTypeFilter]::Launchable },
        [pscustomobject]@{ Name = "Croot d"; Keyword = "C:\ d"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Croot ve"; Keyword = "C:\ ve"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Croot ver"; Keyword = "C:\ ver"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Croot calsupport Config"; Keyword = "C:\ calsupport"; Filter = [MftScanner.SearchTypeFilter]::Config },
        [pscustomobject]@{ Name = "Croot *.log"; Keyword = "C:\ *.log"; Filter = [MftScanner.SearchTypeFilter]::All },
        [pscustomobject]@{ Name = "Croot windows Folder"; Keyword = "C:\ windows"; Filter = [MftScanner.SearchTypeFilter]::Folder }
    )

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($case in $cases) {
        $rows.Add((Invoke-SearchCase -Client $client -Case $case -Phase "cold"))
    }

    $warmup = Wait-SharedHostWarmup -TimeoutSeconds $WarmupTimeoutSeconds
    $shortWarmupDelayMs = 0
    if ($ShortWarmupDelaySeconds -gt 0) {
        $shortWarmupDelayMs = $ShortWarmupDelaySeconds * 1000
        Start-Sleep -Seconds $ShortWarmupDelaySeconds
    }
    foreach ($case in $cases) {
        $rows.Add((Invoke-SearchCase -Client $client -Case $case -Phase "postings-ready"))
    }

    $memoryAfter = @(Get-MftProcessMemory)
    $hostProcessId = if ($hostProcess) { $hostProcess.Id } else { (Read-SharedState).HostProcessId }
    $hostExeWriteTime = if (Test-Path $hostExe) { (Get-Item $hostExe).LastWriteTime } else { [datetime]::MinValue }
    $hostStartTime = [datetime]::MinValue
    if ($hostProcessId -gt 0) {
        try {
            $hostStartTime = (Get-Process -Id $hostProcessId -ErrorAction Stop).StartTime
        }
        catch {
            $hostStartTime = [datetime]::MinValue
        }
    }
    $oldHost = $hostStartTime -ne [datetime]::MinValue -and $hostExeWriteTime -ne [datetime]::MinValue -and $hostStartTime -lt $hostExeWriteTime
    $summary = [ordered]@{
        BuildResult = $buildResult
        IndexedCount = $indexedCount
        ReadyMs = $readySw.ElapsedMilliseconds
        WarmupMs = $warmup.WaitMs
        ShortWarmupDelayMs = $shortWarmupDelayMs
        TrigramReady = $warmup.TrigramReady
        HostReuse = $hostReuse
        OldHost = $oldHost
        HostProcessId = $hostProcessId
        HostStartTime = $hostStartTime
        HostExeWriteTime = $hostExeWriteTime
    }

    $logLines = @(Get-RecentLogLines -LogPath $logPath -Since $startedAt.AddSeconds(-1))
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $jsonPath = Join-Path $outputRoot "mft-full-validation-$timestamp.json"
    $mdPath = Join-Path $outputRoot "mft-full-validation-$timestamp.md"
    $report = [ordered]@{
        StartedAt = $startedAt
        Summary = $summary
        Rows = @($rows.ToArray())
        MemoryBefore = @($memoryBefore)
        MemoryAfter = @($memoryAfter)
        LogPath = $logPath
        LogLines = @($logLines)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-MarkdownReport -Path $mdPath -Summary $summary -Rows @($rows.ToArray()) -MemoryBefore $memoryBefore -MemoryAfter $memoryAfter -LogLines $logLines -JsonPath $jsonPath
    Write-Host "Report: $mdPath"
    if (!$NoOpenReport) { Invoke-Item $mdPath }
}
catch {
    $failure = $_
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $jsonPath = Join-Path $outputRoot "mft-full-validation-failed-$timestamp.json"
    $mdPath = Join-Path $outputRoot "mft-full-validation-failed-$timestamp.md"
    $logLines = @(Get-RecentLogLines -LogPath $logPath -Since $startedAt.AddSeconds(-1))
    $summary = [ordered]@{
        BuildResult = $buildResult
        IndexedCount = 0
        ReadyMs = 0
        WarmupMs = 0
        ShortWarmupDelayMs = 0
        TrigramReady = $false
        HostReuse = $false
        OldHost = $false
        HostProcessId = 0
        Error = $failure.Exception.Message
    }
    $report = [ordered]@{
        StartedAt = $startedAt
        Summary = $summary
        Rows = @()
        MemoryBefore = @()
        MemoryAfter = @(Get-MftProcessMemory)
        LogPath = $logPath
        LogLines = @($logLines)
        Error = $failure.Exception.ToString()
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-MarkdownReport -Path $mdPath -Summary $summary -Rows @() -MemoryBefore @() -MemoryAfter @(Get-MftProcessMemory) -LogLines $logLines -JsonPath $jsonPath
    Write-Host "Failed report: $mdPath"
    if (!$NoOpenReport) { Invoke-Item $mdPath }
    throw
}
finally {
    if ($client) {
        try { $client.Dispose() } catch { }
    }
}
