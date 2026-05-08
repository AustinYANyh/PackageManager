param(
  [string]$Root = ".",
  [string]$ChangesJsonFile = "",
  [string]$CommitMessageFile = "",
  [string]$CommitMessageText = "",
  [string]$CommitMessageLines = "",
  [string]$CommitMessageBase64Utf8 = "",
  [int]$PromptTimeoutSeconds = 30,
  [string]$StateDir = "",
  [ValidateSet("Normal","Hidden","Minimized","Maximized")]
  [string]$WindowStyle = "Normal",
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$CommitMessageLine = @()
)

$ErrorActionPreference = "Stop"

function Quote-ForSingleQuotedPowerShell([string]$value) {
  return $value.Replace("'", "''")
}

$skillRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$runner = Join-Path $skillRoot "scripts\run-commit-push-choice.ps1"
$runner = (Resolve-Path -LiteralPath $runner).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$createdMessageFile = $false
if (-not $StateDir) { $StateDir = Join-Path $skillRoot ".state" }
if (-not [System.IO.Path]::IsPathRooted($StateDir)) {
  $StateDir = Join-Path $rootFull $StateDir
}
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
if (-not $ChangesJsonFile) { $ChangesJsonFile = Join-Path $StateDir "last_changes.json" }
$messageSources = 0
if ($CommitMessageFile) { $messageSources++ }
if ($CommitMessageText) { $messageSources++ }
if ($CommitMessageLines) { $messageSources++ }
if ($CommitMessageBase64Utf8) { $messageSources++ }
if ($CommitMessageLine -and $CommitMessageLine.Count -gt 0) { $messageSources++ }
if ($messageSources -gt 1) {
  throw "CommitMessageFile、CommitMessageText、CommitMessageLines、CommitMessageBase64Utf8 与 CommitMessageLine 只能五选一。"
}

$messageText = $null
if ($CommitMessageBase64Utf8) {
  try {
    $messageBytes = [Convert]::FromBase64String($CommitMessageBase64Utf8)
    $messageText = [System.Text.UTF8Encoding]::new($false, $true).GetString($messageBytes)
  } catch {
    throw "CommitMessageBase64Utf8 不是合法的 UTF-8 Base64 提交日志：$($_.Exception.Message)"
  }
} elseif ($CommitMessageLines) {
  $messageLines = @($CommitMessageLines.Split([string[]]@("__LINE__", "<line>"), [System.StringSplitOptions]::None) | ForEach-Object {
    if ($_ -eq "__BLANK__" -or $_ -eq "<blank>") {
      ""
    } elseif ($_ -like "__BULLET__ *") {
      "- " + $_.Substring(11)
    } elseif ($_ -like "<bullet> *") {
      "- " + $_.Substring(9)
    } else {
      $_
    }
  })
  $messageText = ($messageLines -join "`n")
} elseif ($CommitMessageLine -and $CommitMessageLine.Count -gt 0) {
  $messageLines = @($CommitMessageLine | ForEach-Object {
    if ($_ -eq "__BLANK__" -or $_ -eq "<blank>") {
      ""
    } elseif ($_ -like "__BULLET__ *") {
      "- " + $_.Substring(11)
    } elseif ($_ -like "<bullet> *") {
      "- " + $_.Substring(9)
    } else {
      $_
    }
  })
  $messageText = ($messageLines -join "`n")
} elseif ($CommitMessageText) {
  $messageText = $CommitMessageText.Replace("\r\n", "`n").Replace("\n", "`n").Replace("\r", "`r").Replace("\t", "`t")
}
if ($null -ne $messageText) {
  if ([string]::IsNullOrWhiteSpace($messageText)) {
    throw "提交日志为空，已停止提交。"
  }
  $CommitMessageFile = Join-Path $env:TEMP ("git_svn_commit_message_{0}.txt" -f ([guid]::NewGuid()))
  $createdMessageFile = $true
  [System.IO.File]::WriteAllText($CommitMessageFile, $messageText.TrimEnd("`r", "`n"), [System.Text.UTF8Encoding]::new($false))
} elseif (-not $CommitMessageFile) {
  throw "缺少提交日志。Claude Code 默认必须使用 -CommitMessageBase64Utf8 传入最终提交日志。"
}
$changesFull = (Resolve-Path -LiteralPath $ChangesJsonFile).Path
if (-not (Test-Path -LiteralPath $CommitMessageFile)) {
  throw "找不到提交日志文件：$CommitMessageFile。请使用 -CommitMessageBase64Utf8 传入最终提交日志。"
}
$messageFull = (Resolve-Path -LiteralPath $CommitMessageFile).Path
$out = Join-Path $env:TEMP ("git_svn_commit_push_{0}.json" -f ([guid]::NewGuid()))
$err = Join-Path $env:TEMP ("git_svn_commit_push_{0}.err.txt" -f ([guid]::NewGuid()))

$runnerQ = Quote-ForSingleQuotedPowerShell $runner
$rootQ = Quote-ForSingleQuotedPowerShell $rootFull
$changesQ = Quote-ForSingleQuotedPowerShell $changesFull
$messageQ = Quote-ForSingleQuotedPowerShell $messageFull
$outQ = Quote-ForSingleQuotedPowerShell $out
$errQ = Quote-ForSingleQuotedPowerShell $err
$assumeDefaultArg = ""
if ($WindowStyle -eq "Hidden") {
  $assumeDefaultArg = " -AssumeDefaultChoice"
}

$innerCommand = @"
try {
  `$ErrorActionPreference = 'Stop'
  & '$runnerQ' -Root '$rootQ' -ChangesJsonFile '$changesQ' -CommitMessageFile '$messageQ' -PromptTimeoutSeconds $PromptTimeoutSeconds -ResultJsonFile '$outQ'$assumeDefaultArg -FromWrapper
} catch {
  (`$_ | Out-String) | Set-Content -LiteralPath '$errQ' -Encoding UTF8
  exit 1
}
"@
$encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($innerCommand))

try {
  if ($WindowStyle -eq "Hidden") {
    & $runner -Root $rootFull -ChangesJsonFile $changesFull -CommitMessageFile $messageFull -PromptTimeoutSeconds $PromptTimeoutSeconds -ResultJsonFile $out -AssumeDefaultChoice -FromWrapper *> $null
    $proc = [pscustomobject]@{ ExitCode = 0 }
  } else {
    $proc = Start-Process powershell.exe -WindowStyle $WindowStyle -Wait -PassThru -ArgumentList @(
      "-NoProfile",
      "-ExecutionPolicy", "Bypass",
      "-EncodedCommand", $encodedCommand
    )
  }

  if (-not (Test-Path -LiteralPath $out)) {
    $errText = ""
    if (Test-Path -LiteralPath $err) { $errText = Get-Content -LiteralPath $err -Raw }
    throw "提交推送窗口没有生成 JSON 输出文件：$out`n子进程退出码：$($proc.ExitCode)`n错误输出：$errText"
  }

  Get-Content -LiteralPath $out -Raw
} finally {
  Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $err -Force -ErrorAction SilentlyContinue
  if ($createdMessageFile) {
    Remove-Item -LiteralPath $CommitMessageFile -Force -ErrorAction SilentlyContinue
  }
}
