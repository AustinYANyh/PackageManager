param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryPath,

    [Parameter(Mandatory = $true)]
    [string]$ProjectFile,

    [Parameter(Mandatory = $true)]
    [string]$TargetName,

    [string]$RestorePolicy = 'Auto',

    [string]$NuGetPackagesPath,

    [string[]]$Configurations = @('Debug2024')
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepositoryPath

function Resolve-MSBuild {
    $vswherePaths = @()
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
    if ($programFilesX86) {
        $vswherePaths += Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if ($programFiles) {
        $vswherePaths += Join-Path $programFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
    }

    foreach ($vswherePath in $vswherePaths) {
        if (-not (Test-Path -LiteralPath $vswherePath)) {
            continue
        }

        $vsInstall = & $vswherePath -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if (-not $vsInstall) {
            continue
        }

        $candidate = Join-Path $vsInstall 'MSBuild\Current\Bin\amd64\MSBuild.exe'
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }

        $candidate = Join-Path $vsInstall 'MSBuild\Current\Bin\MSBuild.exe'
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }

        $legacyCandidate = Get-ChildItem -Path (Join-Path $vsInstall 'MSBuild') -Filter 'MSBuild.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($legacyCandidate) {
            return $legacyCandidate.FullName
        }
    }

    $msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($msbuildCommand) {
        return $msbuildCommand.Path
    }

    throw 'MSBuild.exe not found. Install Visual Studio Build Tools with MSBuild, add msbuild.exe to PATH, or install Visual Studio.'
}

function New-SolutionWithoutProjectDependencies([string]$solutionFile) {
    $solutionDirectory = [System.IO.Path]::GetDirectoryName($solutionFile)
    $solutionName = [System.IO.Path]::GetFileNameWithoutExtension($solutionFile)
    $tempSolution = Join-Path $solutionDirectory "$solutionName.__pm_build_$PID.sln"
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $skipProjectDependencies = $false
    $removedSections = 0

    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^\s*ProjectSection\(ProjectDependencies\)') {
            $skipProjectDependencies = $true
            $removedSections++
            continue
        }

        if ($skipProjectDependencies) {
            if ($line -match '^\s*EndProjectSection') {
                $skipProjectDependencies = $false
            }

            continue
        }

        $lines.Add($line)
    }

    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllLines($tempSolution, [string[]]$lines, $encoding)
    Write-Host "Temporary solution: $tempSolution" -ForegroundColor Yellow
    Write-Host "Removed ProjectDependencies section(s): $removedSections" -ForegroundColor Yellow
    return $tempSolution
}

function Get-SolutionConfigurationPlatform([string]$solutionFile, [string]$configuration) {
    $inSection = $false
    $firstMatch = $null
    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^\s*GlobalSection\(SolutionConfigurationPlatforms\)') {
            $inSection = $true
            continue
        }

        if (-not $inSection) {
            continue
        }

        if ($line -match '^\s*EndGlobalSection') {
            break
        }

        if ($line -match '^\s*([^|=]+)\|([^=]+?)\s*=') {
            $solutionConfiguration = $matches[1].Trim()
            $solutionPlatform = $matches[2].Trim()
            if (-not $solutionConfiguration.Equals($configuration, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $candidate = [pscustomobject]@{
                Configuration = $solutionConfiguration
                Platform = $solutionPlatform
            }

            if ($solutionPlatform.Equals('Any CPU', [System.StringComparison]::OrdinalIgnoreCase)) {
                return $candidate
            }

            if (-not $firstMatch) {
                $firstMatch = $candidate
            }
        }
    }

    if ($firstMatch) {
        return $firstMatch
    }

    return [pscustomobject]@{
        Configuration = $configuration
        Platform = 'Any CPU'
    }
}

function Get-ProjectNameFromPath([string]$projectPath) {
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        return $null
    }

    $cleanPath = $projectPath
    $separatorIndex = $cleanPath.IndexOf('::')
    if ($separatorIndex -ge 0) {
        $cleanPath = $cleanPath.Substring(0, $separatorIndex)
    }

    if ($cleanPath.EndsWith('.metaproj', [System.StringComparison]::OrdinalIgnoreCase)) {
        $cleanPath = $cleanPath.Substring(0, $cleanPath.Length - '.metaproj'.Length)
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($cleanPath)
}

function Get-SolutionBuildPlan([string]$solutionFile, [string]$label) {
    if (-not $solutionFile -or -not (Test-Path -LiteralPath $solutionFile)) {
        return $null
    }

    $solutionConfiguration = $label
    $solutionPlatform = $null
    if ($label -match '^(.+)\|(.+)$') {
        $solutionConfiguration = $matches[1]
        $solutionPlatform = $matches[2]
    }

    $projectsByGuid = @{}
    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^Project\("\{[^}]+\}"\)\s*=\s*"([^"]+)",\s*"([^"]+)",\s*"(\{[^}]+\})"') {
            $projectName = $matches[1]
            $projectPath = $matches[2]
            $projectGuid = $matches[3].ToUpperInvariant()
            $extension = [System.IO.Path]::GetExtension($projectPath)
            if ($extension -match '^\.(csproj|vbproj|fsproj|vcxproj)$') {
                $projectsByGuid[$projectGuid] = [pscustomobject]@{
                    Name = $projectName
                    Path = $projectPath
                    HasActiveConfiguration = $false
                    ShouldBuild = $false
                }
            }
        }
    }

    $inSection = $false
    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^\s*GlobalSection\(ProjectConfigurationPlatforms\)') {
            $inSection = $true
            continue
        }

        if (-not $inSection) {
            continue
        }

        if ($line -match '^\s*EndGlobalSection') {
            break
        }

        if ($line -match '^\s*(\{[^}]+\})\.([^|=]+)\|([^.=]+)\.([^=]+)\s*=') {
            $projectGuid = $matches[1].ToUpperInvariant()
            if (-not $projectsByGuid.ContainsKey($projectGuid)) {
                continue
            }

            $entryConfiguration = $matches[2].Trim()
            $entryPlatform = $matches[3].Trim()
            $entryKind = $matches[4].Trim()
            if (-not $entryConfiguration.Equals($solutionConfiguration, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($solutionPlatform -and -not $entryPlatform.Equals($solutionPlatform, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($entryKind.Equals('ActiveCfg', [System.StringComparison]::OrdinalIgnoreCase)) {
                $projectsByGuid[$projectGuid].HasActiveConfiguration = $true
            } elseif ($entryKind.Equals('Build.0', [System.StringComparison]::OrdinalIgnoreCase)) {
                $projectsByGuid[$projectGuid].ShouldBuild = $true
            }
        }
    }

    $projects = @($projectsByGuid.Values)
    $activeProjects = @($projects | Where-Object { $_.HasActiveConfiguration })
    $buildProjects = @($projects | Where-Object { $_.ShouldBuild })
    $skippedProjects = @($activeProjects | Where-Object { -not $_.ShouldBuild })
    return [pscustomobject]@{
        Label = $label
        ProjectCount = $projects.Count
        ActiveProjectCount = $activeProjects.Count
        BuildProjectCount = $buildProjects.Count
        SkippedProjectCount = $skippedProjects.Count
    }
}

function Get-SolutionBuildProjectPaths([string]$solutionFile, [string]$label) {
    if (-not $solutionFile -or -not (Test-Path -LiteralPath $solutionFile)) {
        return @()
    }

    $solutionDirectory = [System.IO.Path]::GetDirectoryName($solutionFile)
    $solutionConfiguration = $label
    $solutionPlatform = $null
    if ($label -match '^(.+)\|(.+)$') {
        $solutionConfiguration = $matches[1]
        $solutionPlatform = $matches[2]
    }

    $projectsByGuid = @{}
    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^Project\("\{[^}]+\}"\)\s*=\s*"([^"]+)",\s*"([^"]+)",\s*"(\{[^}]+\})"') {
            $projectPath = $matches[2]
            $projectGuid = $matches[3].ToUpperInvariant()
            $extension = [System.IO.Path]::GetExtension($projectPath)
            if ($extension -match '^\.(csproj|vbproj|fsproj|vcxproj)$') {
                $fullPath = if ([System.IO.Path]::IsPathRooted($projectPath)) {
                    $projectPath
                } else {
                    Join-Path $solutionDirectory $projectPath
                }
                $projectsByGuid[$projectGuid] = [pscustomobject]@{
                    Path = [System.IO.Path]::GetFullPath($fullPath)
                    ShouldBuild = $false
                }
            }
        }
    }

    $inSection = $false
    foreach ($line in Get-Content -LiteralPath $solutionFile) {
        if ($line -match '^\s*GlobalSection\(ProjectConfigurationPlatforms\)') {
            $inSection = $true
            continue
        }

        if (-not $inSection) {
            continue
        }

        if ($line -match '^\s*EndGlobalSection') {
            break
        }

        if ($line -match '^\s*(\{[^}]+\})\.([^|=]+)\|([^.=]+)\.([^=]+)\s*=') {
            $projectGuid = $matches[1].ToUpperInvariant()
            if (-not $projectsByGuid.ContainsKey($projectGuid)) {
                continue
            }

            $entryConfiguration = $matches[2].Trim()
            $entryPlatform = $matches[3].Trim()
            $entryKind = $matches[4].Trim()
            if (-not $entryConfiguration.Equals($solutionConfiguration, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($solutionPlatform -and -not $entryPlatform.Equals($solutionPlatform, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($entryKind.Equals('Build.0', [System.StringComparison]::OrdinalIgnoreCase)) {
                $projectsByGuid[$projectGuid].ShouldBuild = $true
            }
        }
    }

    return @($projectsByGuid.Values |
        Where-Object { $_.ShouldBuild } |
        Select-Object -ExpandProperty Path)
}

function Get-ProjectTargetFrameworks([string]$projectContent) {
    $frameworks = New-Object 'System.Collections.Generic.List[string]'
    foreach ($match in [regex]::Matches($projectContent, '<TargetFrameworks>\s*([^<]+)\s*</TargetFrameworks>', 'IgnoreCase')) {
        foreach ($framework in $match.Groups[1].Value.Split(';')) {
            if (-not [string]::IsNullOrWhiteSpace($framework)) {
                $frameworks.Add($framework.Trim())
            }
        }
    }

    foreach ($match in [regex]::Matches($projectContent, '<TargetFramework>\s*([^<]+)\s*</TargetFramework>', 'IgnoreCase')) {
        if (-not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
            $frameworks.Add($match.Groups[1].Value.Trim())
        }
    }

    return @($frameworks | Select-Object -Unique)
}

function Test-ProjectNeedsRestore([string]$projectPath) {
    if (-not $projectPath -or -not (Test-Path -LiteralPath $projectPath)) {
        return $false
    }

    $extension = [System.IO.Path]::GetExtension($projectPath)
    if (-not $extension.Equals('.csproj', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $projectContent = Get-Content -LiteralPath $projectPath -Raw
    $usesAssetsFile = $projectContent -match '<PackageReference\b' -or
        $projectContent -match '<TargetFrameworks?>' -or
        $projectContent -match '\sSdk\s*='
    if (-not $usesAssetsFile) {
        return $false
    }

    $assetsPath = Join-Path ([System.IO.Path]::GetDirectoryName($projectPath)) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        return $true
    }

    $frameworks = @(Get-ProjectTargetFrameworks $projectContent)
    if ($frameworks.Count -eq 0) {
        return $false
    }

    $assetsContent = Get-Content -LiteralPath $assetsPath -Raw
    foreach ($framework in $frameworks) {
        if ($assetsContent.IndexOf($framework, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $true
        }
    }

    return $false
}

function Test-BuildNeedsRestore([string]$buildProjectFile, [bool]$solutionBuild, [string]$label) {
    $projectPaths = if ($solutionBuild) {
        @(Get-SolutionBuildProjectPaths $buildProjectFile $label)
    } else {
        @($buildProjectFile)
    }

    foreach ($path in $projectPaths) {
        if (Test-ProjectNeedsRestore $path) {
            return $true
        }
    }

    return $false
}

$buildOutputs = New-Object 'System.Collections.Generic.List[object]'
$buildSummaries = New-Object 'System.Collections.Generic.List[object]'
$buildErrors = New-Object 'System.Collections.Generic.List[object]'
$configurationTimings = New-Object 'System.Collections.Generic.List[object]'

function Add-MSBuildOutputInfo([object[]]$lines, [string]$label) {
    foreach ($rawLine in $lines) {
        if ($null -eq $rawLine) {
            continue
        }

        $line = $rawLine.ToString()
        if ($line -match '^\s*(.+?)\s*->\s*(.+?)\s*$') {
            $buildOutputs.Add([pscustomobject]@{
                Configuration = $label
                Module = $matches[1].Trim()
                Output = $matches[2].Trim()
            })
            continue
        }

        if ($line -match '生成:\s*(\d+)\s*成功[，,]\s*(\d+)\s*失败[，,]\s*(\d+)\s*最新[，,]\s*(\d+)\s*已跳过') {
            $buildSummaries.Add([pscustomobject]@{
                Configuration = $label
                Succeeded = [int]$matches[1]
                Failed = [int]$matches[2]
                UpToDate = [int]$matches[3]
                Skipped = [int]$matches[4]
            })
            continue
        }

        if ($line -match 'Build:\s*(\d+)\s*succeeded[，,]\s*(\d+)\s*failed[，,]\s*(\d+)\s*up-to-date[，,]\s*(\d+)\s*skipped') {
            $buildSummaries.Add([pscustomobject]@{
                Configuration = $label
                Succeeded = [int]$matches[1]
                Failed = [int]$matches[2]
                UpToDate = [int]$matches[3]
                Skipped = [int]$matches[4]
            })
        }

        if ($line -match ':\s*error\s+' -or $line -match '\serror\s+[A-Z]+\d+:') {
            $projectName = $null
            if ($line -match '\[([^\]]+\.(?:csproj|vbproj|fsproj|vcxproj)(?:::[^\]]+)?)\]\s*$') {
                $projectName = Get-ProjectNameFromPath $matches[1]
            } elseif ($line -match '^\s*(.+?\.(?:csproj|vbproj|fsproj|vcxproj)(?:\.metaproj)?)\s*:\s*error') {
                $projectName = Get-ProjectNameFromPath $matches[1]
            }

            $buildErrors.Add([pscustomobject]@{
                Configuration = $label
                Project = $(if ($projectName) { $projectName } else { '<unknown>' })
                Line = $line
            })
        }
    }
}

function Write-BuildReport([int]$exitCode, [datetime]$buildStartTime, [string]$sourceProjectFile, [bool]$solutionBuild) {
    $outputRows = $buildOutputs |
        Group-Object Configuration, Module, Output |
        ForEach-Object { $_.Group[0] } |
        Sort-Object Configuration, Module, Output |
        Select-Object Configuration, Module, Output

    Write-Host ''
    Write-Host '=== Module Outputs ===' -ForegroundColor Cyan
    if ($outputRows) {
        $reportDirectory = Join-Path ([System.IO.Path]::GetDirectoryName($sourceProjectFile)) '.pm-build-reports'
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $csvReportPath = Join-Path $reportDirectory "module-outputs-$stamp.csv"
        $textReportPath = Join-Path $reportDirectory "module-outputs-$stamp.txt"
        $outputRows | Export-Csv -LiteralPath $csvReportPath -NoTypeInformation -Encoding UTF8

        $moduleGroups = @($outputRows |
            Group-Object Configuration, Module |
            Sort-Object Name)

        $textLines = New-Object 'System.Collections.Generic.List[string]'
        foreach ($group in $moduleGroups) {
            $first = $group.Group[0]
            $outputs = @($group.Group | Select-Object -ExpandProperty Output -Unique | Sort-Object)
            $textLines.Add(('[{0}] {1} ({2} output file(s))' -f $first.Configuration, $first.Module, $outputs.Count))
            foreach ($output in $outputs) {
                $textLines.Add(('    - {0}' -f $output))
            }

            $textLines.Add('')
        }
        Set-Content -LiteralPath $textReportPath -Value $textLines -Encoding UTF8

        $index = 1
        foreach ($group in $moduleGroups) {
            $first = $group.Group[0]
            $outputs = @($group.Group | Select-Object -ExpandProperty Output -Unique | Sort-Object)
            Write-Host ('[{0:00}] {1}  ({2}, {3} output file(s))' -f $index, $first.Module, $first.Configuration, $outputs.Count)
            foreach ($output in $outputs) {
                Write-Host ('     - {0}' -f $output)
            }

            $index++
        }

        Write-Host "Module count: $($moduleGroups.Count)" -ForegroundColor Cyan
        Write-Host "Output file count: $($outputRows.Count)" -ForegroundColor Cyan
        Write-Host "CSV report: $csvReportPath" -ForegroundColor Cyan
        Write-Host "Text report: $textReportPath" -ForegroundColor Cyan
    } else {
        Write-Host 'No module output lines were emitted by MSBuild.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host '=== Build Summary ===' -ForegroundColor Cyan
    $totalElapsed = (Get-Date) - $buildStartTime
    Write-Host ('Total elapsed: {0:hh\:mm\:ss\.fff}' -f $totalElapsed) -ForegroundColor Cyan
    if ($configurationTimings.Count -gt 0) {
        foreach ($timing in $configurationTimings) {
            Write-Host ('{0}: {1:hh\:mm\:ss\.fff}, Restore {2}' -f $timing.Configuration, $timing.Elapsed, $timing.RestoreStatus)
        }
    }

    if ($buildSummaries.Count -gt 0) {
        foreach ($summary in $buildSummaries) {
            Write-Host ('{0}: {1} succeeded, {2} failed, {3} up-to-date, {4} skipped' -f
                $summary.Configuration,
                $summary.Succeeded,
                $summary.Failed,
                $summary.UpToDate,
                $summary.Skipped)
        }
    } else {
        $summaryRows = @($outputRows |
            Group-Object Configuration |
            ForEach-Object {
                $configurationName = $_.Name
                $moduleCount = @($_.Group | Select-Object -ExpandProperty Module -Unique).Count
                $failedProjects = @($buildErrors |
                    Where-Object { $_.Configuration -eq $configurationName -and $_.Project -ne '<unknown>' } |
                    Select-Object -ExpandProperty Project -Unique)
                $failedProjectCount = $failedProjects.Count
                if ($failedProjectCount -eq 0 -and $exitCode -ne 0) {
                    $failedProjectCount = 1
                }

                $solutionPlan = $(if ($solutionBuild) { Get-SolutionBuildPlan $sourceProjectFile $configurationName } else { $null })
                $skippedCount = $(if ($solutionPlan) { $solutionPlan.SkippedProjectCount } else { 'Unknown' })
                $upToDateCount = 'Unknown'
                $succeededCount = $moduleCount
                if ($solutionPlan) {
                    $upToDateEstimate = $solutionPlan.BuildProjectCount - $moduleCount - $failedProjectCount
                    if ($upToDateEstimate -lt 0) {
                        $upToDateEstimate = 0
                    }

                    $upToDateCount = "$upToDateEstimate estimated"
                }

                [pscustomobject]@{
                    Configuration = $configurationName
                    Succeeded = $succeededCount
                    OutputFiles = $_.Count
                    Failed = $failedProjectCount
                    UpToDate = $upToDateCount
                    Skipped = $skippedCount
                    IsEstimated = [bool]$solutionPlan
                }
            })
        if ($summaryRows.Count -eq 0) {
            if ($buildErrors.Count -gt 0) {
                $summaryRows = @($buildErrors |
                    Group-Object Configuration |
                    ForEach-Object {
                        $failedProjects = @($_.Group |
                            Where-Object { $_.Project -ne '<unknown>' } |
                            Select-Object -ExpandProperty Project -Unique)
                        $failedProjectCount = $failedProjects.Count
                        if ($failedProjectCount -eq 0) {
                            $failedProjectCount = 1
                        }

                        [pscustomobject]@{
                            Configuration = $_.Name
                            Succeeded = 0
                            OutputFiles = 0
                            Failed = $failedProjectCount
                            UpToDate = 'Unknown'
                            Skipped = 'Unknown'
                            IsEstimated = $false
                        }
                    })
            } else {
                $summaryRows = @([pscustomobject]@{
                    Configuration = ($Configurations -join ', ')
                    Succeeded = 0
                    OutputFiles = 0
                    Failed = $(if ($exitCode -eq 0) { 0 } else { 1 })
                    UpToDate = 'Unknown'
                    Skipped = 'Unknown'
                    IsEstimated = $false
                })
            }
        }

        foreach ($summary in $summaryRows) {
            Write-Host ('{0}: {1} succeeded, {2} failed, {3} up-to-date, {4} skipped, {5} output file(s)' -f
                $summary.Configuration,
                $summary.Succeeded,
                $summary.Failed,
                $summary.UpToDate,
                $summary.Skipped,
                $summary.OutputFiles)
        }
        if ($summaryRows | Where-Object { $_.IsEstimated }) {
            Write-Host 'MSBuild did not emit VS FastUpToDate summary; up-to-date is estimated from solution Build.0 projects minus emitted modules and failures.' -ForegroundColor Yellow
        } else {
            Write-Host 'MSBuild did not emit a VS-style summary; module count and output file count are parsed separately.' -ForegroundColor Yellow
        }
    }

    if ($buildErrors.Count -gt 0) {
        Write-Host ''
        Write-Host '=== Failed Projects / Errors ===' -ForegroundColor Red
        $errorGroups = @($buildErrors |
            Group-Object Configuration, Project |
            Sort-Object Name)
        foreach ($group in $errorGroups) {
            $first = $group.Group[0]
            Write-Host ('[{0}] {1}' -f $first.Configuration, $first.Project) -ForegroundColor Red
            $lines = @($group.Group |
                Select-Object -ExpandProperty Line -Unique |
                Select-Object -First 8)
            foreach ($errorLine in $lines) {
                Write-Host ('     {0}' -f $errorLine) -ForegroundColor Red
            }

            if ($group.Group.Count -gt $lines.Count) {
                Write-Host ('     ... {0} more error line(s)' -f ($group.Group.Count - $lines.Count)) -ForegroundColor Red
            }
        }
    } elseif ($exitCode -ne 0) {
        Write-Host ''
        Write-Host 'MSBuild returned a non-zero exit code, but no error lines were captured.' -ForegroundColor Red
    }
}

$normalizedRestorePolicy = if ($RestorePolicy -ieq 'Always') {
    'Always'
} elseif ($RestorePolicy -ieq 'Never') {
    'Never'
} else {
    'Auto'
}

if (-not $NuGetPackagesPath) {
    $NuGetPackagesPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
}

if (-not $Configurations -or $Configurations.Count -eq 0) {
    $Configurations = @('Debug2024')
}

$msbuildPath = Resolve-MSBuild
$isSolution = [System.IO.Path]::GetExtension($ProjectFile).Equals('.sln', [System.StringComparison]::OrdinalIgnoreCase)
$buildStartTime = Get-Date

New-Item -ItemType Directory -Force -Path $NuGetPackagesPath | Out-Null
$env:NUGET_PACKAGES = $NuGetPackagesPath

Write-Host "Using MSBuild: $msbuildPath" -ForegroundColor Cyan
Write-Host "Project: $ProjectFile" -ForegroundColor Cyan
Write-Host "Target: $TargetName" -ForegroundColor Cyan
Write-Host "Restore policy: $normalizedRestorePolicy" -ForegroundColor Cyan
Write-Host 'Parallel: /m' -ForegroundColor Cyan
Write-Host "NuGet packages: $NuGetPackagesPath" -ForegroundColor Cyan

$buildFile = $ProjectFile
$tempSolution = $null
try {
    if ($isSolution) {
        $tempSolution = New-SolutionWithoutProjectDependencies $ProjectFile
        $buildFile = $tempSolution
    }

    foreach ($configuration in $Configurations) {
        $buildConfiguration = $configuration
        $buildPlatform = $null
        if ($isSolution) {
            $solutionConfigPlatform = Get-SolutionConfigurationPlatform $ProjectFile $configuration
            $buildConfiguration = $solutionConfigPlatform.Configuration
            $buildPlatform = $solutionConfigPlatform.Platform
        }

        $label = $buildConfiguration
        if ($buildPlatform) {
            $label = "$label|$buildPlatform"
        }

        Write-Host ''
        Write-Host "=== $TargetName / $label ===" -ForegroundColor Cyan
        $configurationStartTime = Get-Date
        $restoreStatus = 'Skipped'
        $shouldRestore = $false
        if ($normalizedRestorePolicy.Equals('Always', [System.StringComparison]::OrdinalIgnoreCase)) {
            $shouldRestore = $true
            $restoreStatus = 'Executed (Always)'
        } elseif ($normalizedRestorePolicy.Equals('Auto', [System.StringComparison]::OrdinalIgnoreCase)) {
            $shouldRestore = Test-BuildNeedsRestore $ProjectFile $isSolution $label
            $restoreStatus = $(if ($shouldRestore) { 'Executed (Auto)' } else { 'Skipped (Auto)' })
        } else {
            $restoreStatus = 'Skipped (Never)'
        }

        $buildArgs = @(
            $buildFile,
            "/t:$TargetName",
            "/p:Configuration=$buildConfiguration",
            "/p:RestorePackagesPath=$NuGetPackagesPath",
            '/m',
            '/v:minimal'
        )
        if ($shouldRestore) {
            $buildArgs = @($buildFile, '/restore') + @($buildArgs | Select-Object -Skip 1)
        }
        if ($buildPlatform) {
            $buildArgs += "/p:Platform=$buildPlatform"
        }

        Write-Host "Restore: $restoreStatus" -ForegroundColor Cyan
        Write-Host ("MSBuild args: {0}" -f ($buildArgs -join ' ')) -ForegroundColor DarkCyan
        $capturedOutput = New-Object 'System.Collections.Generic.List[object]'
        & $msbuildPath @buildArgs 2>&1 | ForEach-Object {
            $capturedOutput.Add($_)
            Write-Host $_
        }
        $exitCode = $LASTEXITCODE
        $configurationElapsed = (Get-Date) - $configurationStartTime
        $configurationTimings.Add([pscustomobject]@{
            Configuration = $label
            Elapsed = $configurationElapsed
            RestoreStatus = $restoreStatus
        })
        Add-MSBuildOutputInfo $capturedOutput.ToArray() $label
        if ($exitCode -ne 0) {
            Write-BuildReport $exitCode $buildStartTime $ProjectFile $isSolution
            Write-Host ''
            Write-Host "MSBuild failed with exit code $exitCode." -ForegroundColor Red
            exit $exitCode
        }
    }
} finally {
    if ($tempSolution -and (Test-Path -LiteralPath $tempSolution)) {
        Remove-Item -LiteralPath $tempSolution -Force -ErrorAction SilentlyContinue
    }
}

Write-BuildReport 0 $buildStartTime $ProjectFile $isSolution
Write-Host ''
Write-Host 'All selected configurations completed successfully.' -ForegroundColor Green
