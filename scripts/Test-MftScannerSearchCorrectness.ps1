param(
    [string]$Configuration = "Debug",
    [string[]]$Queries = @("codex", "code", "c", "vs"),
    [int]$MaxResults = 100,
    [int]$ReadyTimeoutSeconds = 180,
    [int]$SearchTimeoutSeconds = 60,
    [switch]$SkipBuild
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

$repoRoot = Resolve-RepoRoot
Set-Location $repoRoot

if (!$SkipBuild) {
    Get-Process MftScanner -ErrorAction SilentlyContinue | Stop-Process -Force
    $msbuild = Resolve-MSBuild
    & $msbuild (Join-Path $repoRoot "PackageManager.csproj") /t:EnsureEmbeddedToolArtifacts "/p:Configuration=$Configuration" /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

$coreDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\MftScanner.Core.dll"
$newtonsoftDll = Join-Path $repoRoot "Tools\MftScanner.Core\bin\$Configuration\Newtonsoft.Json.dll"
if (!(Test-Path $coreDll)) {
    throw "MftScanner.Core.dll was not found: $coreDll"
}

if (Test-Path $newtonsoftDll) {
    [void][System.Reflection.Assembly]::LoadFrom($newtonsoftDll)
}

[void][System.Reflection.Assembly]::LoadFrom($coreDll)

$service = New-Object MftScanner.IndexService
$failures = New-Object System.Collections.Generic.List[string]
$expandedQueries = @(
    foreach ($entry in $Queries) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        foreach ($part in ($entry -split ",")) {
            $trimmed = $part.Trim()
            if (![string]::IsNullOrWhiteSpace($trimmed)) {
                $trimmed
            }
        }
    }
)

try {
    $readyCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($ReadyTimeoutSeconds))
    $indexedCount = $service.BuildIndexAsync($null, $readyCts.Token).GetAwaiter().GetResult()
    $readyCts.Dispose()
    Write-Host "Indexed objects: $indexedCount"

    $records = $service.Index.SortedArray
    $inversions = 0
    for ($i = 1; $i -lt $records.Length; $i++) {
        if ([string]::CompareOrdinal($records[$i - 1].LowerName, $records[$i].LowerName) -gt 0) {
            $inversions++
        }
    }

    Write-Host "Sorted inversions: $inversions"
    if ($inversions -ne 0) {
        $failures.Add("SortedArray is not sorted by LowerName. Inversions=$inversions")
    }

    foreach ($query in $expandedQueries) {
        if ([string]::IsNullOrWhiteSpace($query)) {
            continue
        }

        $normalized = $query.ToLowerInvariant()
        $expected = 0
        foreach ($record in $records) {
            if ($record -ne $null -and
                ![string]::IsNullOrEmpty($record.LowerName) -and
                $record.LowerName.IndexOf($normalized, [StringComparison]::Ordinal) -ge 0) {
                $expected++
            }
        }

        $searchCts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($SearchTimeoutSeconds))
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $result = $service.SearchAsync(
            $query,
            $MaxResults,
            0,
            [MftScanner.SearchTypeFilter]::All,
            $null,
            $searchCts.Token).GetAwaiter().GetResult()
        $sw.Stop()
        $searchCts.Dispose()

        $badNames = @($result.Results | Where-Object {
            $_.FileName.IndexOf($query, [StringComparison]::OrdinalIgnoreCase) -lt 0
        } | Select-Object -First 10 -ExpandProperty FileName)

        $physicalActual = if ($result.PhysicalMatchedCount -gt 0) { $result.PhysicalMatchedCount } else { $result.TotalMatchedCount }
        Write-Host ("Query={0} ExpectedPhysical={1} UI={2} Physical={3} Unique={4} Duplicates={5} Returned={6} BadReturned={7} ElapsedMs={8}" -f `
            $query, $expected, $result.TotalMatchedCount, $physicalActual, $result.UniqueMatchedCount, $result.DuplicatePathCount, @($result.Results).Count, $badNames.Count, $sw.ElapsedMilliseconds)

        if ($physicalActual -ne $expected) {
            $failures.Add("Query '$query' physical total mismatch. Expected=$expected Actual=$physicalActual")
        }

        if ($badNames.Count -gt 0) {
            $failures.Add("Query '$query' returned non-matching file names: $($badNames -join ', ')")
        }
    }
}
finally {
    $service.Shutdown()
}

if ($failures.Count -gt 0) {
    Write-Error ($failures -join [Environment]::NewLine)
    exit 1
}

Write-Host "MftScanner search correctness check passed."
