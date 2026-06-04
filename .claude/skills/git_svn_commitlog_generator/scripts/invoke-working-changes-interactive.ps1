param(
  [string]$Root = ".",
  [int]$PromptTimeoutSeconds = 30,
  [object]$ScanUntrackedForNeedsAdd = $true,
  [object]$IncludeDiff = $true,
  [switch]$NonInteractive,
  [int]$MaxDiffBytesPerFile = 40960,
  [int]$MaxFilesWithDiff = 80,
  [object]$Svn = $true,
  [object]$UseDefaultExcludes = $true,
  [string[]]$AddIds = @(),
  [string[]]$ExcludeIds = @(),
  [string[]]$ExcludePaths = @(),
  [string]$StateDir = "",
  [ValidateSet("Normal","Hidden","Minimized","Maximized")]
  [string]$WindowStyle = "Normal"
)

$ErrorActionPreference = "Stop"

function To-BoolText([object]$value, [string]$defaultValue) {
  if ($null -eq $value) { return $defaultValue }
  if ($value -is [bool]) {
    if ($value) { return "true" }
    return "false"
  }
  $s = $value.ToString().Trim().ToLowerInvariant()
  if ($s -in @("1","true","t","yes","y","on")) { return "true" }
  if ($s -in @("0","false","f","no","n","off")) { return "false" }
  return $defaultValue
}

function ConvertTo-SingleQuotedArrayArgument([string]$ParameterName, [string[]]$Values) {
  $items = @($Values | Where-Object { $null -ne $_ -and $_ -ne "" })
  if ($items.Count -eq 0) { return "" }

  $quoted = @($items | ForEach-Object {
    "'" + (Quote-ForSingleQuotedPowerShell ([string]$_)) + "'"
  })
  return (" -{0} @({1})" -f $ParameterName, ($quoted -join ","))
}

$skillRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$terminalHost = Join-Path $skillRoot "scripts\terminal-host.ps1"
. $terminalHost
$collector = Join-Path $skillRoot "scripts\get-working-changes.ps1"
$collector = (Resolve-Path -LiteralPath $collector).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
if (-not $StateDir) { $StateDir = Join-Path $skillRoot ".state" }
if (-not [System.IO.Path]::IsPathRooted($StateDir)) {
  $StateDir = Join-Path $rootFull $StateDir
}
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$lastChangesFile = Join-Path $StateDir "last_changes.json"
$lastChangesModelFile = Join-Path $StateDir "last_changes_model.json"
$out = Join-Path $env:TEMP ("git_svn_changes_{0}.json" -f ([guid]::NewGuid()))
$err = Join-Path $env:TEMP ("git_svn_changes_{0}.err.txt" -f ([guid]::NewGuid()))

function ConvertTo-ModelProject {
  param([object]$Project)

  [pscustomobject]@{
    Name = $Project.Name
    Scope = $Project.Scope
    Files = @($Project.Files)
    GitFiles = @($Project.GitFiles)
    SvnFiles = @($Project.SvnFiles)
  }
}

function ConvertTo-ModelCommitGroup {
  param([object]$Group)

  [pscustomobject]@{
    Source = $Group.Source
    Key = $Group.Key
    DisplayName = $Group.DisplayName
    GitRepoRoot = $Group.GitRepoRoot
    GitRepoRelRoot = $Group.GitRepoRelRoot
    SvnWcRoot = $Group.SvnWcRoot
    SvnRepoRootUrl = $Group.SvnRepoRootUrl
    SvnRepoUuid = $Group.SvnRepoUuid
    Projects = @($Group.Projects)
    Files = @($Group.Files)
    GitFiles = @($Group.GitFiles)
    SvnFiles = @($Group.SvnFiles)
    Items = @($Group.Items)
  }
}

function ConvertTo-ModelChangesJson {
  param([object]$Changes)

  $diffs = [ordered]@{}
  if ($Changes.Diffs) {
    foreach ($item in @($Changes.ItemsIncludedDefaultLog)) {
      $key = [string]$item.Id
      if ($Changes.Diffs.PSObject.Properties.Match($key).Count -gt 0) {
        $diffs[$key] = $Changes.Diffs.$key
      }
    }
  }

  $model = [pscustomobject]@{
    Root = $Changes.Root
    GeneratedAt = $Changes.GeneratedAt
    Defaults = $Changes.Defaults
    Counts = [pscustomobject]@{
      ItemsAll = @($Changes.ItemsAll).Count
      ItemsIncluded = @($Changes.ItemsIncluded).Count
      ItemsIncludedDefaultLog = @($Changes.ItemsIncludedDefaultLog).Count
      ItemsExcluded = @($Changes.ItemsExcluded).Count
      NeedsAdd = @($Changes.NeedsAdd).Count
      DiffEntries = $diffs.Count
    }
    ItemsIncludedDefaultLog = @($Changes.ItemsIncludedDefaultLog)
    CommitGroupsDefault = @($Changes.CommitGroupsDefault | ForEach-Object { ConvertTo-ModelCommitGroup $_ })
    NeedsAdd = @($Changes.NeedsAdd)
    ItemsExcluded = @($Changes.ItemsExcluded)
    Add = $Changes.Add
    Exclude = $Changes.Exclude
    ProjectsDefault = @($Changes.ProjectsDefault | ForEach-Object { ConvertTo-ModelProject $_ })
    Diffs = $diffs
  }

  return ($model | ConvertTo-Json -Depth 10)
}

$scanText = To-BoolText -value $ScanUntrackedForNeedsAdd -defaultValue "true"
$includeDiffText = To-BoolText -value $IncludeDiff -defaultValue "true"
$svnText = To-BoolText -value $Svn -defaultValue "true"
$useDefaultExcludesText = To-BoolText -value $UseDefaultExcludes -defaultValue "true"
$interactionArg = if ($NonInteractive) { "-NonInteractive" } else { "-Interactive" }

$collectorQ = Quote-ForSingleQuotedPowerShell $collector
$rootQ = Quote-ForSingleQuotedPowerShell $rootFull
$outQ = Quote-ForSingleQuotedPowerShell $out
$errQ = Quote-ForSingleQuotedPowerShell $err
$selectionArgs = ""
$selectionArgs += ConvertTo-SingleQuotedArrayArgument -ParameterName "AddIds" -Values $AddIds
$selectionArgs += ConvertTo-SingleQuotedArrayArgument -ParameterName "ExcludeIds" -Values $ExcludeIds
$selectionArgs += ConvertTo-SingleQuotedArrayArgument -ParameterName "ExcludePaths" -Values $ExcludePaths

$innerCommand = @"
try {
  `$ErrorActionPreference = 'Stop'
  & '$collectorQ' -Root '$rootQ' $interactionArg -PromptTimeoutSeconds $PromptTimeoutSeconds -ScanUntrackedForNeedsAdd $scanText -IncludeDiff $includeDiffText -MaxDiffBytesPerFile $MaxDiffBytesPerFile -MaxFilesWithDiff $MaxFilesWithDiff -Svn $svnText -UseDefaultExcludes $useDefaultExcludesText$selectionArgs | Set-Content -LiteralPath '$outQ' -Encoding UTF8
} catch {
  (`$_ | Out-String) | Set-Content -LiteralPath '$errQ' -Encoding UTF8
  exit 1
}
"@

try {
  $proc = Invoke-InteractivePowerShellScript -ScriptText $innerCommand -WindowStyle $WindowStyle -Title "Git/SVN working changes" -WorkingDirectory $rootFull

  if (-not (Test-Path -LiteralPath $out)) {
    $errText = ""
    if (Test-Path -LiteralPath $err) { $errText = Get-Content -LiteralPath $err -Raw }
    throw "交互窗口没有生成 JSON 输出文件：$out`n子进程退出码：$($proc.ExitCode)`n错误输出：$errText"
  }

  Copy-Item -LiteralPath $out -Destination $lastChangesFile -Force
  $changes = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
  $modelJson = ConvertTo-ModelChangesJson -Changes $changes
  [System.IO.File]::WriteAllText($lastChangesModelFile, $modelJson, [System.Text.UTF8Encoding]::new($false))
  $modelJson
} finally {
  Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $err -Force -ErrorAction SilentlyContinue
}
