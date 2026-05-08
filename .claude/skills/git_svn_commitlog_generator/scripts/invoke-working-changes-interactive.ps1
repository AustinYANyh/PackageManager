param(
  [string]$Root = ".",
  [int]$PromptTimeoutSeconds = 30,
  [object]$ScanUntrackedForNeedsAdd = $true,
  [object]$IncludeDiff = $true,
  [int]$MaxDiffBytesPerFile = 40960,
  [int]$MaxFilesWithDiff = 80,
  [object]$Svn = $true,
  [object]$UseDefaultExcludes = $true,
  [ValidateSet("Normal","Hidden","Minimized","Maximized")]
  [string]$WindowStyle = "Normal"
)

$ErrorActionPreference = "Stop"

function Quote-ForSingleQuotedPowerShell([string]$value) {
  return $value.Replace("'", "''")
}

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

$skillRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$collector = Join-Path $skillRoot "scripts\get-working-changes.ps1"
$collector = (Resolve-Path -LiteralPath $collector).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$out = Join-Path $env:TEMP ("git_svn_changes_{0}.json" -f ([guid]::NewGuid()))
$err = Join-Path $env:TEMP ("git_svn_changes_{0}.err.txt" -f ([guid]::NewGuid()))

$scanText = To-BoolText -value $ScanUntrackedForNeedsAdd -defaultValue "true"
$includeDiffText = To-BoolText -value $IncludeDiff -defaultValue "true"
$svnText = To-BoolText -value $Svn -defaultValue "true"
$useDefaultExcludesText = To-BoolText -value $UseDefaultExcludes -defaultValue "true"

$collectorQ = Quote-ForSingleQuotedPowerShell $collector
$rootQ = Quote-ForSingleQuotedPowerShell $rootFull
$outQ = Quote-ForSingleQuotedPowerShell $out
$errQ = Quote-ForSingleQuotedPowerShell $err

$innerCommand = @"
try {
  `$ErrorActionPreference = 'Stop'
  & '$collectorQ' -Root '$rootQ' -Interactive -PromptTimeoutSeconds $PromptTimeoutSeconds -ScanUntrackedForNeedsAdd $scanText -IncludeDiff $includeDiffText -MaxDiffBytesPerFile $MaxDiffBytesPerFile -MaxFilesWithDiff $MaxFilesWithDiff -Svn $svnText -UseDefaultExcludes $useDefaultExcludesText | Set-Content -LiteralPath '$outQ' -Encoding UTF8
} catch {
  (`$_ | Out-String) | Set-Content -LiteralPath '$errQ' -Encoding UTF8
  exit 1
}
"@
$encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($innerCommand))

try {
  $proc = Start-Process powershell.exe -WindowStyle $WindowStyle -Wait -PassThru -ArgumentList @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-EncodedCommand", $encodedCommand
  )

  if (-not (Test-Path -LiteralPath $out)) {
    $errText = ""
    if (Test-Path -LiteralPath $err) { $errText = Get-Content -LiteralPath $err -Raw }
    throw "交互窗口没有生成 JSON 输出文件：$out`n子进程退出码：$($proc.ExitCode)`n错误输出：$errText"
  }

  Get-Content -LiteralPath $out -Raw
} finally {
  Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $err -Force -ErrorAction SilentlyContinue
}
