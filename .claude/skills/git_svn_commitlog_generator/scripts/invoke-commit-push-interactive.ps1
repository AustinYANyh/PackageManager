[CmdletBinding(PositionalBinding = $false)]
param(
  [string]$Root = ".",
  [string]$ChangesJsonFile = "",
  [string]$CommitMessageFile = "",
  [string]$CommitMessageText = "",
  [string]$CommitMessageLines = "",
  [string]$CommitMessageBase64Utf8 = "",
  [string]$CommitMessageGroupsJsonFile = "",
  [string]$CommitMessageGroupsBase64Utf8 = "",
  [int]$PromptTimeoutSeconds = 30,
  [int]$GitIndexLockStaleMinutes = 10,
  [string]$StateDir = "",
  [ValidateSet("Normal","Hidden","Minimized","Maximized")]
  [string]$WindowStyle = "Normal",
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$CommitMessageLine = @()
)

$ErrorActionPreference = "Stop"

$skillRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$terminalHost = Join-Path $skillRoot "scripts\terminal-host.ps1"
. $terminalHost
$runner = Join-Path $skillRoot "scripts\run-commit-push-choice.ps1"
$runner = (Resolve-Path -LiteralPath $runner).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$createdMessageFile = $false
$createdMessageGroupsFile = $false
if (-not $StateDir -and -not $ChangesJsonFile) {
  throw "缺少 ChangesJsonFile。请使用 Step 1 输出的 LastChangesJsonPath 显式传入 -ChangesJsonFile，或传入同一次采集的 -StateDir。"
}
if (-not $StateDir) { $StateDir = Split-Path -Parent $ChangesJsonFile }
if (-not [System.IO.Path]::IsPathRooted($StateDir)) {
  $StateDir = Join-Path $rootFull $StateDir
}
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$StateDir = (Resolve-Path -LiteralPath $StateDir).Path
if (-not $ChangesJsonFile) { $ChangesJsonFile = Join-Path $StateDir "last_changes.json" }
$messageSources = 0
if ($CommitMessageFile) { $messageSources++ }
if ($CommitMessageText) { $messageSources++ }
if ($CommitMessageLines) { $messageSources++ }
if ($CommitMessageBase64Utf8) { $messageSources++ }
if ($CommitMessageLine -and $CommitMessageLine.Count -gt 0) { $messageSources++ }
if ($messageSources -gt 1) {
  throw "CommitMessageFile、CommitMessageText、CommitMessageLines、CommitMessageBase64Utf8 与 CommitMessageLine 只能五选一。CommitMessageLine 是数组入口，不能重复写同一个命名参数；自动化默认应使用 CommitMessageBase64Utf8。"
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
  throw "缺少提交日志。模型/自动化默认必须使用 -CommitMessageBase64Utf8 传入最终提交日志。"
}

if ($CommitMessageGroupsJsonFile -and $CommitMessageGroupsBase64Utf8) {
  throw "CommitMessageGroupsJsonFile 与 CommitMessageGroupsBase64Utf8 只能二选一。"
}
if ($CommitMessageGroupsBase64Utf8) {
  try {
    $groupBytes = [Convert]::FromBase64String($CommitMessageGroupsBase64Utf8)
    $groupJson = [System.Text.UTF8Encoding]::new($false, $true).GetString($groupBytes)
    if ([string]::IsNullOrWhiteSpace($groupJson)) {
      throw "提交组日志 JSON 为空。"
    }
    $null = $groupJson | ConvertFrom-Json
    $CommitMessageGroupsJsonFile = Join-Path $env:TEMP ("git_svn_commit_message_groups_{0}.json" -f ([guid]::NewGuid()))
    $createdMessageGroupsFile = $true
    [System.IO.File]::WriteAllText($CommitMessageGroupsJsonFile, $groupJson, [System.Text.UTF8Encoding]::new($false))
  } catch {
    throw "CommitMessageGroupsBase64Utf8 不是合法的 UTF-8 Base64 JSON：$($_.Exception.Message)"
  }
}
$changesFull = (Resolve-Path -LiteralPath $ChangesJsonFile).Path
if (-not (Test-Path -LiteralPath $CommitMessageFile)) {
  throw "找不到提交日志文件：$CommitMessageFile。模型/自动化请使用 -CommitMessageBase64Utf8 传入最终提交日志。"
}
$messageFull = (Resolve-Path -LiteralPath $CommitMessageFile).Path
$messageGroupsFull = ""
if ($CommitMessageGroupsJsonFile) {
  $messageGroupsFull = (Resolve-Path -LiteralPath $CommitMessageGroupsJsonFile).Path
}
$out = Join-Path $env:TEMP ("git_svn_commit_push_{0}.json" -f ([guid]::NewGuid()))
$err = Join-Path $env:TEMP ("git_svn_commit_push_{0}.err.txt" -f ([guid]::NewGuid()))

$runnerQ = Quote-ForSingleQuotedPowerShell $runner
$rootQ = Quote-ForSingleQuotedPowerShell $rootFull
$changesQ = Quote-ForSingleQuotedPowerShell $changesFull
$messageQ = Quote-ForSingleQuotedPowerShell $messageFull
$messageGroupsArg = ""
if ($messageGroupsFull) {
  $messageGroupsQ = Quote-ForSingleQuotedPowerShell $messageGroupsFull
  $messageGroupsArg = " -CommitMessageGroupsJsonFile '$messageGroupsQ'"
}
$outQ = Quote-ForSingleQuotedPowerShell $out
$errQ = Quote-ForSingleQuotedPowerShell $err
$assumeDefaultArg = ""
if ($WindowStyle -eq "Hidden") {
  $assumeDefaultArg = " -AssumeDefaultChoice"
}

$innerCommand = @"
try {
  `$ErrorActionPreference = 'Stop'
  & '$runnerQ' -Root '$rootQ' -ChangesJsonFile '$changesQ' -CommitMessageFile '$messageQ'$messageGroupsArg -PromptTimeoutSeconds $PromptTimeoutSeconds -GitIndexLockStaleMinutes $GitIndexLockStaleMinutes -ResultJsonFile '$outQ'$assumeDefaultArg -FromWrapper
} catch {
  (`$_ | Out-String) | Set-Content -LiteralPath '$errQ' -Encoding UTF8
  exit 1
}
"@

try {
  if ($WindowStyle -eq "Hidden") {
    $runnerArgs = @{
      Root = $rootFull
      ChangesJsonFile = $changesFull
      CommitMessageFile = $messageFull
      PromptTimeoutSeconds = $PromptTimeoutSeconds
      GitIndexLockStaleMinutes = $GitIndexLockStaleMinutes
      ResultJsonFile = $out
      AssumeDefaultChoice = $true
      FromWrapper = $true
    }
    if ($messageGroupsFull) {
      $runnerArgs.CommitMessageGroupsJsonFile = $messageGroupsFull
    }
    & $runner @runnerArgs *> $null
    $proc = [pscustomobject]@{ ExitCode = 0 }
  } else {
    $proc = Invoke-InteractivePowerShellScript -ScriptText $innerCommand -WindowStyle $WindowStyle -Title "Git/SVN commit and push" -WorkingDirectory $rootFull
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
  if ($createdMessageGroupsFile) {
    Remove-Item -LiteralPath $CommitMessageGroupsJsonFile -Force -ErrorAction SilentlyContinue
  }
}
