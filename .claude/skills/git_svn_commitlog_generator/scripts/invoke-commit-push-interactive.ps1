param(
  [string]$Root = ".",
  [Parameter(Mandatory = $true)][string]$ChangesJsonFile,
  [Parameter(Mandatory = $true)][string]$CommitMessageFile,
  [int]$PromptTimeoutSeconds = 30,
  [ValidateSet("Normal","Hidden","Minimized","Maximized")]
  [string]$WindowStyle = "Normal"
)

$ErrorActionPreference = "Stop"

function Quote-ForSingleQuotedPowerShell([string]$value) {
  return $value.Replace("'", "''")
}

$skillRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$runner = Join-Path $skillRoot "scripts\run-commit-push-choice.ps1"
$runner = (Resolve-Path -LiteralPath $runner).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$changesFull = (Resolve-Path -LiteralPath $ChangesJsonFile).Path
$messageFull = (Resolve-Path -LiteralPath $CommitMessageFile).Path
$out = Join-Path $env:TEMP ("git_svn_commit_push_{0}.json" -f ([guid]::NewGuid()))
$err = Join-Path $env:TEMP ("git_svn_commit_push_{0}.err.txt" -f ([guid]::NewGuid()))

$runnerQ = Quote-ForSingleQuotedPowerShell $runner
$rootQ = Quote-ForSingleQuotedPowerShell $rootFull
$changesQ = Quote-ForSingleQuotedPowerShell $changesFull
$messageQ = Quote-ForSingleQuotedPowerShell $messageFull
$outQ = Quote-ForSingleQuotedPowerShell $out
$errQ = Quote-ForSingleQuotedPowerShell $err

$innerCommand = @"
try {
  `$ErrorActionPreference = 'Stop'
  & '$runnerQ' -Root '$rootQ' -ChangesJsonFile '$changesQ' -CommitMessageFile '$messageQ' -PromptTimeoutSeconds $PromptTimeoutSeconds -FromWrapper | Set-Content -LiteralPath '$outQ' -Encoding UTF8
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
    throw "提交推送窗口没有生成 JSON 输出文件：$out`n子进程退出码：$($proc.ExitCode)`n错误输出：$errText"
  }

  Get-Content -LiteralPath $out -Raw
} finally {
  Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $err -Force -ErrorAction SilentlyContinue
}
