param(
    [string]$Configuration = "Debug",
    [string]$OutputDirectory = ".\artifacts\mft-full-validation",
    [int]$ReadyTimeoutSeconds = 180,
    [int]$WarmupTimeoutSeconds = 70,
    [int]$ShortWarmupDelaySeconds = 4,
    [int]$SearchTimeoutSeconds = 90,
    [int]$RealtimeUsnTimeoutSeconds = 15,
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

function Get-MftScannerHostProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'MftScanner.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -match '(^| )--index-agent( |$)'
        } |
        Select-Object ProcessId, CommandLine)
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

function Invoke-RealtimeSearchCount {
    param(
        [object]$Client,
        [string]$Keyword
    )

    $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
    try {
        $result = $Client.SearchAsync($Keyword, 50, 0, [MftScanner.SearchTypeFilter]::All, $null, $cts.Token).GetAwaiter().GetResult()
        [pscustomobject]@{
            Count = [int]$result.TotalMatchedCount
            Returned = @($result.Results).Count
            HostMs = [int64]$result.HostSearchMs
            Error = ""
        }
    }
    catch {
        [pscustomobject]@{
            Count = -1
            Returned = 0
            HostMs = -1
            Error = $_.Exception.Message
        }
    }
    finally {
        $cts.Dispose()
    }
}

function Wait-RealtimeSearchCount {
    param(
        [object]$Client,
        [string]$Keyword,
        [int]$ExpectedCount,
        [int]$TimeoutSeconds
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = $null
    $timeoutMs = [Math]::Max(1, $TimeoutSeconds) * 1000
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $last = Invoke-RealtimeSearchCount -Client $Client -Keyword $Keyword
        if ($last.Count -eq $ExpectedCount) {
            $sw.Stop()
            return [pscustomobject]@{
                Ok = $true
                Ms = $sw.ElapsedMilliseconds
                Expected = $ExpectedCount
                Count = $last.Count
                Returned = $last.Returned
                HostMs = $last.HostMs
                Error = $last.Error
            }
        }

        Start-Sleep -Milliseconds 100
    }

    $sw.Stop()
    if ($null -eq $last) {
        $last = Invoke-RealtimeSearchCount -Client $Client -Keyword $Keyword
    }

    [pscustomobject]@{
        Ok = $false
        Ms = $sw.ElapsedMilliseconds
        Expected = $ExpectedCount
        Count = $last.Count
        Returned = $last.Returned
        HostMs = $last.HostMs
        Error = $last.Error
    }
}

function Invoke-RealtimeUsnSmokeTest {
    param(
        [object]$Client,
        [int]$TimeoutSeconds
    )

    $token = "mft_realtime_smoke_{0}" -f (Get-Date -Format "yyyyMMddHHmmssfff")
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) "mft-realtime-smoke"
    $filePath = Join-Path $directory ($token + ".txt")
    $copyPath = Join-Path $directory ($token + " - copy.txt")
    $atomicFinalPath = Join-Path $directory ($token + "_MakeNumberConfig.cs")
    $atomicTempPath = Join-Path $directory ($token + "_MakeNumberConfig.cs~RF5b95bd.TME")
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $baseline = Invoke-RealtimeSearchCount -Client $Client -Keyword $token
    $created = $null
    $deleted = $null
    $recreated = $null
    $copied = $null
    $atomicSaved = $null
    $atomicTempGone = $null
    try {
        Remove-Item -LiteralPath $filePath, $copyPath, $atomicFinalPath, $atomicTempPath -Force -ErrorAction SilentlyContinue

        Set-Content -LiteralPath $filePath -Value "created" -Encoding UTF8
        $created = Wait-RealtimeSearchCount -Client $Client -Keyword $token -ExpectedCount ($baseline.Count + 1) -TimeoutSeconds $TimeoutSeconds

        Remove-Item -LiteralPath $filePath -Force
        $deleted = Wait-RealtimeSearchCount -Client $Client -Keyword $token -ExpectedCount $baseline.Count -TimeoutSeconds $TimeoutSeconds

        Set-Content -LiteralPath $filePath -Value "recreated" -Encoding UTF8
        $recreated = Wait-RealtimeSearchCount -Client $Client -Keyword $token -ExpectedCount ($baseline.Count + 1) -TimeoutSeconds $TimeoutSeconds

        Copy-Item -LiteralPath $filePath -Destination $copyPath -Force
        $copied = Wait-RealtimeSearchCount -Client $Client -Keyword $token -ExpectedCount ($baseline.Count + 2) -TimeoutSeconds $TimeoutSeconds

        Set-Content -LiteralPath $atomicTempPath -Value "atomic-save" -Encoding UTF8
        Move-Item -LiteralPath $atomicTempPath -Destination $atomicFinalPath -Force
        $atomicSaved = Wait-RealtimeSearchCount -Client $Client -Keyword $token -ExpectedCount ($baseline.Count + 3) -TimeoutSeconds $TimeoutSeconds
        $atomicTempGone = Wait-RealtimeSearchCount -Client $Client -Keyword ([System.IO.Path]::GetFileName($atomicTempPath)) -ExpectedCount 0 -TimeoutSeconds $TimeoutSeconds
    }
    finally {
        Remove-Item -LiteralPath $filePath, $copyPath, $atomicFinalPath, $atomicTempPath -Force -ErrorAction SilentlyContinue
    }

    $healthy = $baseline.Count -ge 0 `
        -and $created -and $created.Ok `
        -and $deleted -and $deleted.Ok `
        -and $recreated -and $recreated.Ok `
        -and $copied -and $copied.Ok `
        -and $atomicSaved -and $atomicSaved.Ok `
        -and $atomicTempGone -and $atomicTempGone.Ok

    [pscustomobject]@{
        Healthy = [bool]$healthy
        TimeoutSeconds = $TimeoutSeconds
        Token = $token
        Directory = $directory
        Baseline = $baseline
        Created = $created
        Deleted = $deleted
        Recreated = $recreated
        Copied = $copied
        AtomicSaved = $atomicSaved
        AtomicTempGone = $atomicTempGone
    }
}

function Get-RecentLogLines {
    param(
        [string]$LogPath,
        [datetime]$Since
    )

    if (!(Test-Path $LogPath)) { return @() }
    $patterns = "\[SNAPSHOT RESTORE TOTAL\]|\[SNAPSHOT RESTORE\]|\[SNAPSHOT LOAD\]|\[POSTINGS SNAPSHOT RESTORE\]|\[CONTAINS WARMUP\]|\[CONTAINS SHORT GENERIC\]|\[CONTAINS BIGRAM COUNT BUILD\]|\[APPLY BATCH\]|\[LIVE DELTA COMPACT\]|\[SNAPSHOT CATCHUP TOTAL\]|\[SNAPSHOT CATCHUP LIVE APPLY\]|\[PATH PREFILTER\]|\[CONTAINS QUERY\]|\[MMF\]|\[MMF HOST\]|\[WATCHER BATCH\]|\[WATCHER BATCH APPLY\]|\[WATCHER READ SLICE\]|\[USN RENAME_NEW UPSERT\]|\[USN PROVISIONAL\]|\[CREATE UPSERT\]|\[DELETE OVERLAY\]|\[DELETE OVERLAY RESTORE\]"
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
        [object]$RealtimeUsn,
        [string[]]$LogLines,
        [string]$JsonPath
    )

    if ($null -eq $RealtimeUsn) {
        $RealtimeUsn = [pscustomobject]@{
            Healthy = $false
            Token = ""
            Directory = ""
            Created = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
            Deleted = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
            Recreated = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
            Copied = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
            AtomicSaved = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
            AtomicTempGone = [pscustomobject]@{ Ok = $false; Ms = 0; Expected = 0; Count = 0; Returned = 0; HostMs = 0; Error = "" }
        }
    }

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
    $lines.Add("- HostProcessCount：$($Summary.HostProcessCount)，HostGuardOk=$($Summary.HostGuardOk)")
    $lines.Add("- RealtimeUSN：Healthy=$($Summary.RealtimeUsnHealthy)，MaxMs=$($Summary.RealtimeUsnMaxMs)，Timeout=$($Summary.RealtimeUsnTimeoutSeconds)s")
    $validRows = @($Rows | Where-Object { -not $_.Error })
    if ($validRows.Count -gt 0) {
        $hostValues = @($validRows | ForEach-Object { [int64]$_.HostMs } | Sort-Object)
        $clientValues = @($validRows | ForEach-Object { [int64]$_.ClientMs } | Sort-Object)
        $hostP50 = $hostValues[[Math]::Min($hostValues.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($hostValues.Count * 0.50) - 1))]
        $hostP95 = $hostValues[[Math]::Min($hostValues.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($hostValues.Count * 0.95) - 1))]
        $hostMax = ($hostValues | Measure-Object -Maximum).Maximum
        $clientP95 = $clientValues[[Math]::Min($clientValues.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($clientValues.Count * 0.95) - 1))]
        $lines.Add("- 查询统计：Host P50=$hostP50 ms，Host P95=$hostP95 ms，Host Max=$hostMax ms，Client P95=$clientP95 ms")
    }
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
    $lines.Add("## USN 实时冒烟")
    $lines.Add("")
    $lines.Add("- Token：``$($RealtimeUsn.Token)``")
    $lines.Add("- 临时目录：``$($RealtimeUsn.Directory)``")
    $lines.Add("- Healthy：$($RealtimeUsn.Healthy)")
    $lines.Add("")
    $lines.Add("| 操作 | 成功 | 耗时(ms) | 期望数量 | 实际数量 | 返回 | 搜索宿主(ms) | 错误 |")
    $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |")
    $steps = @(
        [pscustomobject]@{ Name = "Create"; Row = $RealtimeUsn.Created },
        [pscustomobject]@{ Name = "Delete"; Row = $RealtimeUsn.Deleted },
        [pscustomobject]@{ Name = "Recreate"; Row = $RealtimeUsn.Recreated },
        [pscustomobject]@{ Name = "Copy"; Row = $RealtimeUsn.Copied },
        [pscustomobject]@{ Name = "AtomicSave"; Row = $RealtimeUsn.AtomicSaved },
        [pscustomobject]@{ Name = "AtomicTempGone"; Row = $RealtimeUsn.AtomicTempGone }
    )
    foreach ($step in $steps) {
        $row = $step.Row
        $err = if ($row -and $row.Error) { $row.Error.ToString().Replace("|", "/") } else { "" }
        $lines.Add("| $($step.Name) | $($row.Ok) | $($row.Ms) | $($row.Expected) | $($row.Count) | $($row.Returned) | $($row.HostMs) | $err |")
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
    Start-Sleep -Milliseconds 500
    $hostProcesses = @(Get-MftScannerHostProcesses)
    $hostGuardOk = $hostProcesses.Count -le 1
    if (-not $hostGuardOk) {
        throw "Index host guard failed: found $($hostProcesses.Count) MftScanner.exe --index-agent processes: $(@($hostProcesses | ForEach-Object { $_.ProcessId }) -join ', ')"
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

    $genericSingleChars = @("x", "q", "z", "1", "_")
    foreach ($token in $genericSingleChars) {
        $cases += [pscustomobject]@{ Name = "Generic 1char $token"; Keyword = $token; Filter = [MftScanner.SearchTypeFilter]::All }
        $cases += [pscustomobject]@{ Name = "Croot 1char $token"; Keyword = "C:\ $token"; Filter = [MftScanner.SearchTypeFilter]::All }
    }

    $genericBigrams = @("on", "ex", "zz", "ui", "ar")
    foreach ($token in $genericBigrams) {
        $cases += [pscustomobject]@{ Name = "Generic 2char $token"; Keyword = $token; Filter = [MftScanner.SearchTypeFilter]::All }
        $cases += [pscustomobject]@{ Name = "Croot 2char $token"; Keyword = "C:\ $token"; Filter = [MftScanner.SearchTypeFilter]::All }
    }

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

    $realtimeUsn = Invoke-RealtimeUsnSmokeTest -Client $client -TimeoutSeconds $RealtimeUsnTimeoutSeconds
    $realtimeUsnStepMs = @(
        if ($realtimeUsn.Created) { [int64]$realtimeUsn.Created.Ms }
        if ($realtimeUsn.Deleted) { [int64]$realtimeUsn.Deleted.Ms }
        if ($realtimeUsn.Recreated) { [int64]$realtimeUsn.Recreated.Ms }
        if ($realtimeUsn.Copied) { [int64]$realtimeUsn.Copied.Ms }
        if ($realtimeUsn.AtomicSaved) { [int64]$realtimeUsn.AtomicSaved.Ms }
        if ($realtimeUsn.AtomicTempGone) { [int64]$realtimeUsn.AtomicTempGone.Ms }
    )
    $realtimeUsnMaxMs = if ($realtimeUsnStepMs.Count -gt 0) {
        ($realtimeUsnStepMs | Measure-Object -Maximum).Maximum
    }
    else {
        0
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
    $hostProcessesAfter = @(Get-MftScannerHostProcesses)
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
        HostProcessCount = $hostProcessesAfter.Count
        HostGuardOk = $hostProcessesAfter.Count -le 1
        HostStartTime = $hostStartTime
        HostExeWriteTime = $hostExeWriteTime
        RealtimeUsnHealthy = $realtimeUsn.Healthy
        RealtimeUsnMaxMs = $realtimeUsnMaxMs
        RealtimeUsnTimeoutSeconds = $RealtimeUsnTimeoutSeconds
    }

    $logLines = @(Get-RecentLogLines -LogPath $logPath -Since $startedAt.AddSeconds(-1))
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $jsonPath = Join-Path $outputRoot "mft-full-validation-$timestamp.json"
    $mdPath = Join-Path $outputRoot "mft-full-validation-$timestamp.md"
    $report = [ordered]@{
        StartedAt = $startedAt
        Summary = $summary
        Rows = @($rows.ToArray())
        RealtimeUsn = $realtimeUsn
        MemoryBefore = @($memoryBefore)
        MemoryAfter = @($memoryAfter)
        LogPath = $logPath
        LogLines = @($logLines)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-MarkdownReport -Path $mdPath -Summary $summary -Rows @($rows.ToArray()) -MemoryBefore $memoryBefore -MemoryAfter $memoryAfter -RealtimeUsn $realtimeUsn -LogLines $logLines -JsonPath $jsonPath
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
        HostProcessCount = @(Get-MftScannerHostProcesses).Count
        HostGuardOk = $false
        RealtimeUsnHealthy = $false
        RealtimeUsnMaxMs = 0
        RealtimeUsnTimeoutSeconds = $RealtimeUsnTimeoutSeconds
        Error = $failure.Exception.Message
    }
    $report = [ordered]@{
        StartedAt = $startedAt
        Summary = $summary
        Rows = @()
        RealtimeUsn = $null
        MemoryBefore = @()
        MemoryAfter = @(Get-MftProcessMemory)
        LogPath = $logPath
        LogLines = @($logLines)
        Error = $failure.Exception.ToString()
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-MarkdownReport -Path $mdPath -Summary $summary -Rows @() -MemoryBefore @() -MemoryAfter @(Get-MftProcessMemory) -RealtimeUsn $null -LogLines $logLines -JsonPath $jsonPath
    Write-Host "Failed report: $mdPath"
    if (!$NoOpenReport) { Invoke-Item $mdPath }
    throw
}
finally {
    if ($client) {
        try { $client.Dispose() } catch { }
    }
}
