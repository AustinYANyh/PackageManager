param(
  [string]$Root = ".",
  [Parameter(Mandatory = $true)][string]$ChangesJsonFile,
  [Parameter(Mandatory = $true)][string]$CommitMessageFile,
  [string]$CommitMessageGroupsJsonFile = "",
  [int]$PromptTimeoutSeconds = 30,
  [int]$GitIndexLockStaleMinutes = 10,
  [string]$ResultJsonFile = "",
  [switch]$AssumeDefaultChoice,
  [switch]$FromWrapper
)

$ErrorActionPreference = "Stop"
if (-not $FromWrapper.IsPresent) {
  throw "请不要直接调用 run-commit-push-choice.ps1；模型必须调用 invoke-commit-push-interactive.ps1，由 wrapper 打开并等待提交推送交互窗口。"
}
try {
  [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
  $OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
}

function Test-WindowsConsoleInput {
  try {
    if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { return $false }
    if (-not ($IsWindows -or ($env:OS -match 'Windows'))) { return $false }
    if ([Console]::IsInputRedirected) { return $false }
    return $true
  } catch { return $false }
}

function Invoke-TimedChoiceKey {
  param(
    [ValidatePattern('^[1-9A-Z]+$')][string]$Choices = "12",
    [int]$TimeoutSec = 30,
    [char]$DefaultKey = '1',
    [string]$Message = "Select"
  )
  if ($AssumeDefaultChoice.IsPresent) { return 1 }
  if (-not (Test-WindowsConsoleInput)) { return 1 }
  if ($TimeoutSec -lt 1) { $TimeoutSec = 1 }

  $choiceChars = $Choices.ToCharArray()
  $defaultIndex = [Array]::IndexOf($choiceChars, $DefaultKey)
  if ($defaultIndex -lt 0) { $defaultIndex = 0 }

  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  $lastLen = 0
  while ($true) {
    $remaining = [Math]::Max(0, [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds))
    $line = ("{0}（剩余 {1} 秒，超时自动选 {2}）： " -f $Message, $remaining, $choiceChars[$defaultIndex])
    $pad = if ($lastLen -gt $line.Length) { " " * ($lastLen - $line.Length) } else { "" }
    Write-Host -NoNewline ("`r{0}{1}" -f $line, $pad)
    $lastLen = $line.Length

    if ($remaining -le 0) {
      Write-Host ""
      return ($defaultIndex + 1)
    }

    $until = [DateTime]::UtcNow.AddMilliseconds(100)
    while ([DateTime]::UtcNow -lt $until) {
      if ([Console]::KeyAvailable) {
        $key = [Console]::ReadKey($true)
        $pressed = [char]::ToUpperInvariant($key.KeyChar)
        for ($i = 0; $i -lt $choiceChars.Length; $i++) {
          if ([char]::ToUpperInvariant($choiceChars[$i]) -eq $pressed) {
            Write-Host $key.KeyChar
            return ($i + 1)
          }
        }
      }
      Start-Sleep -Milliseconds 20
    }
  }
}

function Read-TimedConsoleLine {
  param(
    [string]$Prompt,
    [int]$TimeoutSec = 30
  )
  if ($AssumeDefaultChoice.IsPresent) { return "" }
  if (-not (Test-WindowsConsoleInput)) { return "" }
  if ($TimeoutSec -lt 1) { $TimeoutSec = 1 }

  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  $chars = New-Object System.Collections.Generic.List[char]
  $lastLen = 0
  while ([DateTime]::UtcNow -lt $deadline) {
    $remaining = [Math]::Max(0, [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds))
    $line = ("{0}（输入编号后按回车；剩余 {1} 秒，超时=不选择）： {2}" -f $Prompt, $remaining, (-join $chars))
    $pad = if ($lastLen -gt $line.Length) { " " * ($lastLen - $line.Length) } else { "" }
    Write-Host -NoNewline ("`r{0}{1}" -f $line, $pad)
    $lastLen = $line.Length

    if (-not [Console]::KeyAvailable) {
      Start-Sleep -Milliseconds 100
      continue
    }

    $key = [Console]::ReadKey($true)
    if ($key.Key -eq [ConsoleKey]::Enter) {
      Write-Host ""
      return -join $chars
    }

    if ($key.Key -eq [ConsoleKey]::Backspace) {
      if ($chars.Count -gt 0) { $chars.RemoveAt($chars.Count - 1) }
      continue
    }

    if (-not [char]::IsControl($key.KeyChar)) {
      $chars.Add($key.KeyChar)
    }
  }

  Write-Host ""
  return -join $chars
}

function Read-BlockingReviewFeedback {
  Write-Host ""
  Write-Host "请输入对提交日志的修改意见，输入后直接回车结束。" -ForegroundColor Yellow
  Write-Host "此步骤不设超时，模型会逐字读取你的意见并重新生成日志。" -ForegroundColor Yellow

  $line = Read-Host -Prompt ">"
  if ($null -eq $line) { return "" }

  return $line.Trim()
}

function Expand-IdTokens([string]$raw) {
  $ids = New-Object System.Collections.Generic.List[int]
  foreach ($tok in ($raw -split '[,\s;]+')) {
    if (-not $tok) { continue }
    if ($tok -match '^(\d+)-(\d+)$') {
      $lo = [int]$Matches[1]; $hi = [int]$Matches[2]
      if ($lo -gt $hi) { $lo, $hi = $hi, $lo }
      for ($i = $lo; $i -le $hi; $i++) { $ids.Add($i) }
    } else {
      $tv = 0
      if ([int]::TryParse($tok, [ref]$tv)) { $ids.Add($tv) }
    }
  }
  return ,$ids
}

function Quote-NativeArgument([string]$value) {
  if ($null -eq $value) { return '""' }
  if ($value -eq "") { return '""' }
  if ($value -notmatch '[\s"`]') { return $value }
  return '"' + (($value -replace '\\(?=")', '$0$0') -replace '"', '\"') + '"'
}

function Get-Utf8Sha256([string]$value) {
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($value)
  return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
}

function ConvertTo-JsonString([object]$value) {
  if ($null -eq $value) { return "null" }
  $text = [string]$value
  $text = $text.Replace("\", "\\").Replace('"', '\"').Replace("`r", "\r").Replace("`n", "\n").Replace("`t", "\t")
  return '"' + $text + '"'
}

function ConvertTo-JsonStringArray([object[]]$values) {
  if ($null -eq $values -or $values.Count -eq 0) { return "[]" }
  return "[" + ((@($values) | ForEach-Object { ConvertTo-JsonString $_ }) -join ",") + "]"
}

function ConvertTo-CommandJson([object]$command) {
  $argsJson = ConvertTo-JsonStringArray @($command.args)
  $outputJson = ConvertTo-JsonStringArray @($command.output)
  return ('{{"tool":{0},"args":{1},"exitCode":{2},"output":{3}}}' -f (ConvertTo-JsonString $command.tool), $argsJson, ([int]$command.exitCode), $outputJson)
}

function ConvertTo-GroupJson([object]$group) {
  $pathsJson = ConvertTo-JsonStringArray @($group.Paths)
  $errorsJson = ConvertTo-JsonStringArray @($group.Errors)
  $commandsJson = "[]"
  if ($group.Commands -and @($group.Commands).Count -gt 0) {
    $commandsJson = "[" + ((@($group.Commands) | ForEach-Object { ConvertTo-CommandJson $_ }) -join ",") + "]"
  }
  $repoRoot = if ($group.Source -eq "git") { $group.GitRepoRoot } else { "" }
  $wcRoot = if ($group.Source -eq "svn") { $group.SvnWcRoot } else { "" }
  $fileCount = if ($group.Items) { @($group.Items).Count } else { 0 }
  return ('{{"GroupId":{0},"Source":{1},"DisplayName":{2},"GitRepoRoot":{3},"SvnWcRoot":{4},"FileCount":{5},"Paths":{6},"Status":{7},"Errors":{8},"Commands":{9},"CommitMessageSha256":{10},"CommitMessage":{11}}}' -f `
    ([int]$group.GroupId),
    (ConvertTo-JsonString $group.Source),
    (ConvertTo-JsonString $group.DisplayName),
    (ConvertTo-JsonString $repoRoot),
    (ConvertTo-JsonString $wcRoot),
    ([int]$fileCount),
    $pathsJson,
    (ConvertTo-JsonString $group.Status),
    $errorsJson,
    $commandsJson,
    (ConvertTo-JsonString $group.CommitMessageSha256),
    (ConvertTo-JsonString $group.CommitMessage))
}

function ConvertTo-ResultJson([object]$value) {
  $errorsJson = ConvertTo-JsonStringArray @($value.Errors)
  $commandsJson = "[]"
  if ($value.Commands -and @($value.Commands).Count -gt 0) {
    $commandsJson = "[" + ((@($value.Commands) | ForEach-Object { ConvertTo-CommandJson $_ }) -join ",") + "]"
  }
  $groupsJson = "[]"
  if ($value.Groups -and @($value.Groups).Count -gt 0) {
    $groupsJson = "[" + ((@($value.Groups) | ForEach-Object { ConvertTo-GroupJson $_ }) -join ",") + "]"
  }
  return ('{{"Status":{0},"Choice":{1},"GitPathCount":{2},"SvnPathCount":{3},"Errors":{4},"Commands":{5},"CommitMessageSha256":{6},"CommitMessage":{7},"ReviewFeedback":{8},"ReviewFeedbackRaw":{9},"Groups":{10}}}' -f `
    (ConvertTo-JsonString $value.Status),
    ([int]$value.Choice),
    ([int]$value.GitPathCount),
    ([int]$value.SvnPathCount),
    $errorsJson,
    $commandsJson,
    (ConvertTo-JsonString $value.CommitMessageSha256),
    (ConvertTo-JsonString $value.CommitMessage),
    (ConvertTo-JsonString $value.ReviewFeedback),
    (ConvertTo-JsonString $value.ReviewFeedbackRaw),
    $groupsJson)
}

function Invoke-LoggedCommand {
  param(
    [string]$Tool,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [hashtable]$Environment = @{},
    [int]$TimeoutSeconds = 0
  )

  Write-Host ("> {0} {1}" -f $Tool, (($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join " ")) -ForegroundColor DarkCyan

  $resolvedTool = @(Get-Command -Name $Tool -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
  if (-not $resolvedTool) {
    $message = "找不到命令：$Tool。请确认它已安装并加入 PATH；这类问题按 exitCode=127 返回。"
    Write-Host $message -ForegroundColor Red
    return [pscustomobject]@{
      tool = $Tool
      args = @($Arguments)
      exitCode = 127
      output = @($message)
    }
  }

  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $resolvedTool.Source
  $psi.Arguments = (@($Arguments) | ForEach-Object { Quote-NativeArgument $_ }) -join " "
  $psi.WorkingDirectory = $WorkingDirectory
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
  $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
  foreach ($key in $Environment.Keys) {
    $psi.EnvironmentVariables[$key] = [string]$Environment[$key]
  }

  $proc = New-Object System.Diagnostics.Process
  $proc.StartInfo = $psi
  $stdoutLines = New-Object System.Collections.Generic.List[string]
  $stderrLines = New-Object System.Collections.Generic.List[string]
  try {
    [void]$proc.Start()
    $stdoutText = ""
    $stderrText = ""
    if ($TimeoutSeconds -gt 0) {
      if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch {}
        Write-Host ("命令超时，已终止：{0}" -f $Tool) -ForegroundColor Red
        $exitCode = 124
      } else {
        $stdoutText = $proc.StandardOutput.ReadToEnd()
        $stderrText = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $exitCode = $proc.ExitCode
      }
    } else {
      $stdoutText = $proc.StandardOutput.ReadToEnd()
      $stderrText = $proc.StandardError.ReadToEnd()
      $proc.WaitForExit()
      $exitCode = $proc.ExitCode
    }

    foreach ($line in @($stdoutText -split "`r?`n")) {
      if (-not [string]::IsNullOrWhiteSpace($line)) {
        $stdoutLines.Add($line)
      }
    }
    foreach ($line in @($stderrText -split "`r?`n")) {
      if (-not [string]::IsNullOrWhiteSpace($line)) {
        $stderrLines.Add($line)
      }
    }
  } catch {
    $message = $_ | Out-String
    Write-Host $message -ForegroundColor Red
    $exitCode = 1
    if ($_.Exception -is [System.ComponentModel.Win32Exception] -and $_.Exception.NativeErrorCode -eq 2) {
      $exitCode = 127
    }
    if (-not [string]::IsNullOrWhiteSpace($message)) {
      $stderrLines.Add($message.Trim())
    }
  }

  $combinedOutput = New-Object System.Collections.Generic.List[string]
  foreach ($line in $stdoutLines) { $combinedOutput.Add($line) }
  foreach ($line in $stderrLines) { $combinedOutput.Add($line) }

  return [pscustomobject]@{
    tool = $Tool
    args = @($Arguments)
    exitCode = $exitCode
    output = @($combinedOutput)
  }
}

function Test-GitIndexLockError([object]$command) {
  if ($null -eq $command) { return $false }
  if ([int]$command.exitCode -eq 0) { return $false }
  $joined = (@($command.output) -join "`n")
  if (-not $joined) { return $false }
  return $joined -match 'index\.lock' -and $joined -match 'Unable to create|File exists|another git process'
}

function Get-GitLockRelatedProcesses {
  $results = @()
  try {
    $procs = Get-Process -ErrorAction SilentlyContinue | Where-Object {
      $name = $_.ProcessName
      return ($name -ieq "git" -or $name -ieq "ssh" -or $name -like "git-remote-*")
    }
    foreach ($proc in @($procs)) {
      $path = ""
      try { $path = $proc.Path } catch {}
      $started = ""
      try { $started = $proc.StartTime.ToString("s") } catch {}
      $results += [pscustomobject]@{
        Id = $proc.Id
        ProcessName = $proc.ProcessName
        StartTime = $started
        Path = $path
      }
    }
  } catch {
  }
  return @($results)
}

function Format-GitLockProcessSummary([object[]]$processes) {
  if ($null -eq $processes -or @($processes).Count -eq 0) {
    return "无相关 git 进程"
  }

  return ((@($processes) | ForEach-Object {
    $parts = @("PID=$($_.Id)", $_.ProcessName)
    if ($_.StartTime) { $parts += "Start=$($_.StartTime)" }
    if ($_.Path) { $parts += "Path=$($_.Path)" }
    $parts -join " "
  }) -join "; ")
}

function Get-GitIndexLockPath([string]$RepoRoot) {
  $dotGit = Join-Path $RepoRoot ".git"
  if (Test-Path -LiteralPath $dotGit -PathType Container) {
    return Join-Path $dotGit "index.lock"
  }

  if (Test-Path -LiteralPath $dotGit -PathType Leaf) {
    try {
      $firstLine = Get-Content -LiteralPath $dotGit -TotalCount 1 -ErrorAction Stop
      if ($firstLine -match '^gitdir:\s*(.+)\s*$') {
        $gitDir = $Matches[1].Trim()
        if (-not [System.IO.Path]::IsPathRooted($gitDir)) {
          $gitDir = Join-Path $RepoRoot $gitDir
        }
        return Join-Path ([System.IO.Path]::GetFullPath($gitDir)) "index.lock"
      }
    } catch {
    }
  }

  return Join-Path $dotGit "index.lock"
}

function Get-GitIndexLockInfo([string]$RepoRoot, [int]$StaleMinutes) {
  $lockPath = Get-GitIndexLockPath -RepoRoot $RepoRoot
  if (-not (Test-Path -LiteralPath $lockPath)) {
    return [pscustomobject]@{
      Exists = $false
      LockPath = $lockPath
      LastWriteTime = $null
      AgeMinutes = 0.0
      IsStale = $false
      RelatedProcesses = @()
      RelatedProcessSummary = "无相关 git 进程"
    }
  }

  $item = Get-Item -LiteralPath $lockPath -ErrorAction Stop
  $now = Get-Date
  $age = $now - $item.LastWriteTime
  $related = Get-GitLockRelatedProcesses
  return [pscustomobject]@{
    Exists = $true
    LockPath = $item.FullName
    LastWriteTime = $item.LastWriteTime
    AgeMinutes = [Math]::Round($age.TotalMinutes, 2)
    IsStale = $age.TotalMinutes -ge $StaleMinutes
    RelatedProcesses = @($related)
    RelatedProcessSummary = Format-GitLockProcessSummary -processes $related
  }
}

function Try-RecoverGitIndexLock([string]$RepoRoot, [int]$StaleMinutes) {
  $info = Get-GitIndexLockInfo -RepoRoot $RepoRoot -StaleMinutes $StaleMinutes
  if (-not $info.Exists) {
    return [pscustomobject]@{
      Recovered = $false
      Message = "未发现 .git/index.lock。"
      Info = $info
    }
  }

  if (-not $info.IsStale) {
    $msg = "检测到 Git index.lock，但锁龄仅 $($info.AgeMinutes) 分钟，小于阈值 $StaleMinutes 分钟；不自动删除。锁文件：$($info.LockPath)。进程：$($info.RelatedProcessSummary)"
    return [pscustomobject]@{
      Recovered = $false
      Message = $msg
      Info = $info
    }
  }

  if (@($info.RelatedProcesses).Count -gt 0) {
    $msg = "检测到陈旧 Git index.lock，但存在相关 git 进程；不自动删除。锁文件：$($info.LockPath)。锁龄：$($info.AgeMinutes) 分钟。进程：$($info.RelatedProcessSummary)"
    return [pscustomobject]@{
      Recovered = $false
      Message = $msg
      Info = $info
    }
  }

  try {
    Remove-Item -LiteralPath $info.LockPath -Force -ErrorAction Stop
    return [pscustomobject]@{
      Recovered = $true
      Message = "已清理陈旧 Git index.lock 并准备重试。锁文件：$($info.LockPath)。锁龄：$($info.AgeMinutes) 分钟。"
      Info = $info
    }
  } catch {
    $msg = "检测到陈旧 Git index.lock，但删除失败：$($_.Exception.Message)。锁文件：$($info.LockPath)。锁龄：$($info.AgeMinutes) 分钟。"
    return [pscustomobject]@{
      Recovered = $false
      Message = $msg
      Info = $info
    }
  }
}

function Invoke-GitCommandWithIndexLockRecovery {
  param(
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [hashtable]$Environment = @{},
    [int]$TimeoutSeconds = 0,
    [int]$StaleMinutes = 10
  )

  $cmd = Invoke-LoggedCommand -Tool "git" -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Environment $Environment -TimeoutSeconds $TimeoutSeconds
  if (-not (Test-GitIndexLockError -command $cmd)) {
    return @($cmd)
  }

  $recovery = Try-RecoverGitIndexLock -RepoRoot $WorkingDirectory -StaleMinutes $StaleMinutes
  $firstOutput = @($cmd.output)
  $firstOutput += $recovery.Message
  $cmd = [pscustomobject]@{
    tool = $cmd.tool
    args = @($cmd.args)
    exitCode = $cmd.exitCode
    output = @($firstOutput)
  }

  if (-not $recovery.Recovered) {
    return @($cmd)
  }

  Write-Host $recovery.Message -ForegroundColor Yellow
  $retry = Invoke-LoggedCommand -Tool "git" -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Environment $Environment -TimeoutSeconds $TimeoutSeconds
  $retryOutput = @($retry.output)
  $retryOutput = @($recovery.Message) + $retryOutput
  $retry = [pscustomobject]@{
    tool = $retry.tool
    args = @($retry.args)
    exitCode = $retry.exitCode
    output = @($retryOutput)
  }

  return @($cmd, $retry)
}

function Test-GitNoUpstreamPushError([object]$Command) {
  if ($null -eq $Command) { return $false }
  $text = (@($Command.output) -join "`n")
  return ($text -match '(?i)no upstream branch|has no upstream branch|set-upstream')
}

function Add-GitCommandOutputMessage([object]$Command, [string]$Message) {
  $output = @()
  if ($null -ne $Command) { $output += @($Command.output) }
  if ($Message) { $output += $Message }
  return [pscustomobject]@{
    tool = if ($null -ne $Command) { $Command.tool } else { "git" }
    args = if ($null -ne $Command) { @($Command.args) } else { @("push") }
    exitCode = if ($null -ne $Command) { $Command.exitCode } else { 128 }
    output = @($output)
  }
}

function New-GitPushResult([object[]]$Commands, [object]$FinalCommand) {
  return [pscustomobject]@{
    Commands = @($Commands)
    FinalCommand = $FinalCommand
  }
}

function Invoke-GitPushWithUpstreamRetry {
  param(
    [string]$WorkingDirectory,
    [int]$GitIndexLockStaleMinutes
  )

  $gitEnv = @{ GIT_TERMINAL_PROMPT = "0" }
  $commands = @()
  $cmds = @(Invoke-GitCommandWithIndexLockRecovery -Arguments @("-c", "credential.interactive=false", "push") -WorkingDirectory $WorkingDirectory -Environment $gitEnv -TimeoutSeconds 120 -StaleMinutes $GitIndexLockStaleMinutes)
  $commands += $cmds
  $pushCmd = $cmds[-1]
  if ($pushCmd.exitCode -eq 0 -or -not (Test-GitNoUpstreamPushError -Command $pushCmd)) {
    return (New-GitPushResult -Commands $commands -FinalCommand $pushCmd)
  }

  Write-Host "当前分支没有 upstream，准备自动设置 origin 上游分支后重试 push..." -ForegroundColor Yellow
  $branchCmd = Invoke-LoggedCommand -Tool "git" -Arguments @("rev-parse", "--abbrev-ref", "HEAD") -WorkingDirectory $WorkingDirectory
  $commands += $branchCmd
  if ($branchCmd.exitCode -ne 0) {
    $final = Add-GitCommandOutputMessage -Command $pushCmd -Message "无法自动设置 upstream：获取当前分支失败。"
    return (New-GitPushResult -Commands $commands -FinalCommand $final)
  }

  $branch = (@($branchCmd.output) | Where-Object { $_ } | Select-Object -First 1)
  if ($branch) { $branch = $branch.Trim() }
  if (-not $branch -or $branch -eq "HEAD") {
    $final = Add-GitCommandOutputMessage -Command $pushCmd -Message "无法自动设置 upstream：当前不在普通本地分支（detached HEAD）。"
    return (New-GitPushResult -Commands $commands -FinalCommand $final)
  }

  $remoteCmd = Invoke-LoggedCommand -Tool "git" -Arguments @("remote") -WorkingDirectory $WorkingDirectory
  $commands += $remoteCmd
  if ($remoteCmd.exitCode -ne 0) {
    $final = Add-GitCommandOutputMessage -Command $pushCmd -Message "无法自动设置 upstream：读取 Git remote 列表失败。"
    return (New-GitPushResult -Commands $commands -FinalCommand $final)
  }

  $hasOrigin = @($remoteCmd.output | ForEach-Object { if ($_) { $_.Trim() } } | Where-Object { $_ -eq "origin" }).Count -gt 0
  if (-not $hasOrigin) {
    $final = Add-GitCommandOutputMessage -Command $pushCmd -Message "无法自动设置 upstream：仓库没有 origin remote。"
    return (New-GitPushResult -Commands $commands -FinalCommand $final)
  }

  Write-Host ("正在执行 git push --set-upstream origin {0}..." -f $branch) -ForegroundColor Cyan
  $retryCmds = @(Invoke-GitCommandWithIndexLockRecovery -Arguments @("-c", "credential.interactive=false", "push", "--set-upstream", "origin", $branch) -WorkingDirectory $WorkingDirectory -Environment $gitEnv -TimeoutSeconds 120 -StaleMinutes $GitIndexLockStaleMinutes)
  $commands += $retryCmds
  return (New-GitPushResult -Commands $commands -FinalCommand $retryCmds[-1])
}

function Get-ItemTextProperty([object]$Item, [string]$Name) {
  if ($null -eq $Item) { return "" }
  if ($Item.PSObject.Properties.Match($Name).Count -eq 0) { return "" }
  return [string]$Item.$Name
}

function Get-SvnCommitGroups([object[]]$Items, [string]$RootFull) {
  $buckets = @{}
  foreach ($item in @($Items)) {
    $wcRoot = Get-ItemTextProperty -Item $item -Name "SvnWcRoot"
    if (-not $wcRoot) { continue }

    $repoUuid = Get-ItemTextProperty -Item $item -Name "SvnRepoUuid"
    $repoRootUrl = Get-ItemTextProperty -Item $item -Name "SvnRepoRootUrl"
    $repoKey = if ($repoUuid) { $repoUuid } elseif ($repoRootUrl) { $repoRootUrl } else { $wcRoot }

    if (-not $buckets.ContainsKey($repoKey)) {
      $buckets[$repoKey] = [pscustomobject]@{
        Key = $repoKey
        WcRoot = $wcRoot
        Items = @()
      }
    } elseif ([string]$buckets[$repoKey].WcRoot -ne [string]$wcRoot) {
      $currentRoot = [string]$buckets[$repoKey].WcRoot
      if ($currentRoot.Length -gt $wcRoot.Length -and $currentRoot.StartsWith($wcRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $buckets[$repoKey].WcRoot = $wcRoot
      }
    }

    $buckets[$repoKey].Items = @($buckets[$repoKey].Items) + $item
  }

  return @($buckets.Values)
}

function Get-NormalizedPath([string]$Path) {
  if (-not $Path) { return "" }
  try {
    return ([System.IO.Path]::GetFullPath($Path)).TrimEnd('\','/')
  } catch {
    return $Path.TrimEnd('\','/')
  }
}

function Get-GitCommitGroups([object[]]$Items, [string]$RootFull) {
  $buckets = @{}
  foreach ($item in @($Items)) {
    $repoRoot = Get-ItemTextProperty -Item $item -Name "GitRepoRoot"
    if (-not $repoRoot) { $repoRoot = $RootFull }
    $repoRoot = Get-NormalizedPath $repoRoot
    $repoRelRoot = Get-ItemTextProperty -Item $item -Name "GitRepoRelRoot"
    $repoPath = Get-ItemTextProperty -Item $item -Name "GitRepoPath"
    if (-not $repoPath) {
      $repoPath = Get-ItemTextProperty -Item $item -Name "Path"
      if ($repoRelRoot -and $repoPath.StartsWith(($repoRelRoot.TrimEnd('/') + "/"), [System.StringComparison]::OrdinalIgnoreCase)) {
        $repoPath = $repoPath.Substring($repoRelRoot.TrimEnd('/').Length + 1)
      }
    }
    $repoPathsForItem = @($repoPath)
    $renamedFromPath = Get-ItemTextProperty -Item $item -Name "GitRepoRenamedFrom"
    if ($renamedFromPath -and ($renamedFromPath -ne $repoPath)) {
      $repoPathsForItem += $renamedFromPath
      $primaryFull = Join-Path -Path $repoRoot -ChildPath ($repoPath -replace '/', '\')
      $renamedFull = Join-Path -Path $repoRoot -ChildPath ($renamedFromPath -replace '/', '\')
      if ((-not (Test-Path -LiteralPath $primaryFull)) -and (Test-Path -LiteralPath $renamedFull)) {
        $repoPathsForItem = @($renamedFromPath, $repoPath)
      }
    }

    if (-not $buckets.ContainsKey($repoRoot)) {
      $display = if ($repoRelRoot) { $repoRelRoot } else { Split-Path -Leaf $repoRoot }
      $buckets[$repoRoot] = [pscustomobject]@{
        Key = ("git|{0}" -f $repoRoot.ToLowerInvariant())
        Source = "git"
        DisplayName = $display
        GitRepoRoot = $repoRoot
        GitRepoRelRoot = $repoRelRoot
        Items = @()
        RepoPaths = @()
      }
    }

    $buckets[$repoRoot].Items = @($buckets[$repoRoot].Items) + $item
    $buckets[$repoRoot].RepoPaths = @($buckets[$repoRoot].RepoPaths) + $repoPathsForItem
  }

  return @($buckets.Values)
}

function Get-MessageGroupKey([object]$MessageGroup) {
  $source = Get-ItemTextProperty -Item $MessageGroup -Name "Source"
  if ($source -eq "git") {
    $repoRoot = Get-ItemTextProperty -Item $MessageGroup -Name "GitRepoRoot"
    if ($repoRoot) { return ("git|{0}" -f (Get-NormalizedPath $repoRoot).ToLowerInvariant()) }
  }

  if ($source -eq "svn") {
    $uuid = Get-ItemTextProperty -Item $MessageGroup -Name "SvnRepoUuid"
    if ($uuid) { return "svn|$uuid" }
    $url = Get-ItemTextProperty -Item $MessageGroup -Name "SvnRepoRootUrl"
    if ($url) { return "svn|$url" }
    $wcRoot = Get-ItemTextProperty -Item $MessageGroup -Name "SvnWcRoot"
    if ($wcRoot) { return ("svn|{0}" -f (Get-NormalizedPath $wcRoot).ToLowerInvariant()) }
  }

  return ""
}

function ConvertTo-CommitGroups([object[]]$GitGroups, [object[]]$SvnGroups, [object[]]$MessageGroups, [string]$DefaultMessage) {
  $messageByKey = @{}
  foreach ($mg in @($MessageGroups)) {
    $key = Get-MessageGroupKey -MessageGroup $mg
    $msg = Get-ItemTextProperty -Item $mg -Name "CommitMessage"
    if ($key -and $msg) { $messageByKey[$key] = $msg.TrimEnd("`r", "`n") }
  }

  $groups = New-Object System.Collections.Generic.List[object]
  $id = 1
  foreach ($g in @($GitGroups | Sort-Object GitRepoRoot)) {
    $hasGroupMessage = $messageByKey.ContainsKey($g.Key)
    $message = if ($hasGroupMessage) { $messageByKey[$g.Key] } else { $DefaultMessage }
    [void]$groups.Add([pscustomobject]@{
      GroupId = $id
      Source = "git"
      DisplayName = $g.DisplayName
      GitRepoRoot = $g.GitRepoRoot
      GitRepoRelRoot = $g.GitRepoRelRoot
      MessageKey = $g.Key
      HasGroupMessage = $hasGroupMessage
      Items = @($g.Items | Sort-Object Path)
      Paths = @($g.RepoPaths | Sort-Object -Unique)
      CommitMessage = $message
      CommitMessageSha256 = Get-Utf8Sha256 $message
      Status = "not_started"
      Commands = @()
      Errors = @()
    })
    $id += 1
  }

  foreach ($g in @($SvnGroups | Sort-Object WcRoot)) {
    $first = @($g.Items | Select-Object -First 1)[0]
    $key = if ($first) {
      $uuid = Get-ItemTextProperty -Item $first -Name "SvnRepoUuid"
      $url = Get-ItemTextProperty -Item $first -Name "SvnRepoRootUrl"
      if ($uuid) { "svn|$uuid" } elseif ($url) { "svn|$url" } else { ("svn|{0}" -f (Get-NormalizedPath $g.WcRoot).ToLowerInvariant()) }
    } else { "" }
    $hasGroupMessage = ($key -and $messageByKey.ContainsKey($key))
    $message = if ($hasGroupMessage) { $messageByKey[$key] } else { $DefaultMessage }
    [void]$groups.Add([pscustomobject]@{
      GroupId = $id
      Source = "svn"
      DisplayName = (Split-Path -Leaf $g.WcRoot)
      SvnWcRoot = $g.WcRoot
      MessageKey = $key
      HasGroupMessage = $hasGroupMessage
      Items = @($g.Items | Sort-Object Path)
      Paths = @($g.Items | ForEach-Object { Join-Path -Path $RootFull -ChildPath ($_.Path -replace '/', '\') } | Sort-Object -Unique)
      CommitMessage = $message
      CommitMessageSha256 = Get-Utf8Sha256 $message
      Status = "not_started"
      Commands = @()
      Errors = @()
    })
    $id += 1
  }

  return $groups.ToArray()
}

$rootFull = (Resolve-Path -LiteralPath $Root).Path
$changes = Get-Content -LiteralPath $ChangesJsonFile -Raw | ConvertFrom-Json
$messageText = Get-Content -LiteralPath $CommitMessageFile -Raw
$items = @($changes.ItemsIncludedDefaultLog)
$messageGroups = @()
if ($CommitMessageGroupsJsonFile -and (Test-Path -LiteralPath $CommitMessageGroupsJsonFile)) {
  $messageGroupsJson = Get-Content -LiteralPath $CommitMessageGroupsJsonFile -Raw
  if (-not [string]::IsNullOrWhiteSpace($messageGroupsJson)) {
    $decoded = $messageGroupsJson | ConvertFrom-Json
    if ($decoded.PSObject.Properties.Match("Groups").Count -gt 0) {
      $messageGroups = @($decoded.Groups)
    } else {
      $messageGroups = @($decoded)
    }
  }
}

$gitItems = @($items | Where-Object { $_.Source -eq "git" })
$svnItems = @($items | Where-Object { $_.Source -eq "svn" })
$gitGroups = Get-GitCommitGroups -Items $gitItems -RootFull $rootFull
$svnGroups = Get-SvnCommitGroups -Items $svnItems -RootFull $rootFull
$commitGroups = ConvertTo-CommitGroups -GitGroups $gitGroups -SvnGroups $svnGroups -MessageGroups $messageGroups -DefaultMessage $messageText
$gitGroupCount = @($commitGroups | Where-Object { $_.Source -eq "git" }).Count
$svnGroupCount = @($commitGroups | Where-Object { $_.Source -eq "svn" }).Count
$totalFileCount = 0
foreach ($group in @($commitGroups)) { $totalFileCount += @($group.Items).Count }

if (@($commitGroups).Count -gt 1) {
  $missingGroupMessages = @($commitGroups | Where-Object { -not $_.HasGroupMessage })
  if ($missingGroupMessages.Count -gt 0) {
    $errors = @($missingGroupMessages | ForEach-Object {
      "多提交组必须为每个提交组提供专属提交日志，缺少提交组 [{0}] {1} {2} 的 CommitMessage（匹配键：{3}）。请通过 -CommitMessageGroupsBase64Utf8 传入 Groups[].CommitMessage。" -f `
        $_.GroupId, $_.Source.ToUpperInvariant(), $_.DisplayName, $_.MessageKey
    })
    foreach ($group in @($commitGroups)) {
      if (-not $group.HasGroupMessage) {
        $group.Status = "failed"
        $group.Errors = @($group.Errors) + "缺少专属提交日志"
      } else {
        $group.Status = "not_started"
      }
    }

    Write-Host ""
    foreach ($errorMessage in $errors) {
      Write-Host $errorMessage -ForegroundColor Red
    }

    $result = [pscustomobject]@{
      Status = "failed"
      Choice = 0
      GitPathCount = @($gitItems).Count
      SvnPathCount = $svnItems.Count
      Errors = @($errors)
      Commands = @()
      CommitMessageSha256 = Get-Utf8Sha256 $messageText
      CommitMessage = $messageText
      Groups = @($commitGroups)
    }

    if ($ResultJsonFile) {
      $json = ConvertTo-ResultJson $result
      [System.IO.File]::WriteAllText($ResultJsonFile, $json, [System.Text.UTF8Encoding]::new($false))
    } else {
      ConvertTo-ResultJson $result
    }
    return
  }
}

Write-Host ""
Write-Host "步骤 3/3：是否现在帮你提交并推送？" -ForegroundColor Cyan
Write-Host "操作说明：直接按 1 = 提交全部提交组（默认）；按 2 = 选择提交组；按 3 = 暂不提交；按 4 = 提出意见重新生成日志。" -ForegroundColor Yellow
Write-Host "超时规则：${PromptTimeoutSeconds} 秒内不按键，自动选择 1，执行全部提交组。" -ForegroundColor Yellow
Write-Host ""
Write-Host ("本次包含 {0} 个提交组：Git 仓库 {1} 个，SVN 提交组 {2} 个，总文件 {3} 个。" -f @($commitGroups).Count, $gitGroupCount, $svnGroupCount, $totalFileCount) -ForegroundColor Cyan
foreach ($group in @($commitGroups | Sort-Object GroupId)) {
  Write-Host ""
  $kindLabel = if ($group.Source -eq "svn") { "SVN 提交组" } elseif ($group.Source -eq "git") { "GIT 仓库" } else { $group.Source.ToUpperInvariant() }
  Write-Host ("[{0}] {1}  {2}  {3} 个文件" -f $group.GroupId, $kindLabel, $group.DisplayName, @($group.Items).Count) -ForegroundColor Cyan
  if ($group.Source -eq "git") {
    Write-Host ("    Repo: {0}" -f $group.GitRepoRoot) -ForegroundColor DarkGray
  } else {
    Write-Host ("    WC:   {0}" -f $group.SvnWcRoot) -ForegroundColor DarkGray
  }
  Write-Host ("    SHA256: {0}" -f $group.CommitMessageSha256) -ForegroundColor DarkGray
  Write-Host "    提交日志：" -ForegroundColor Cyan
  foreach ($line in ($group.CommitMessage -split "`r?`n")) {
    Write-Host ("      {0}" -f $line)
  }
  Write-Host "    文件：" -ForegroundColor Cyan
  foreach ($item in @($group.Items)) {
    Write-Host ("      {0}" -f $item.Path)
  }
}
Write-Host ""

$choice = Invoke-TimedChoiceKey -Choices "1234" -TimeoutSec $PromptTimeoutSeconds -DefaultKey '1' -Message "请选择：1提交全部提交组 2选择提交组 3暂不提交 4提出意见重新生成日志"
$commands = @()
$status = "skipped"
$errors = @()
$reviewFeedback = ""
$selectedGroupIds = New-Object System.Collections.Generic.HashSet[int]

if ($choice -eq 1) {
  foreach ($group in @($commitGroups)) { [void]$selectedGroupIds.Add([int]$group.GroupId) }
} elseif ($choice -eq 2) {
  Write-Host "请输入要提交的组编号，多个编号用逗号/空格分隔，支持范围（如 1-3），例如：1,3" -ForegroundColor Yellow
  $line = Read-TimedConsoleLine -Prompt "要提交的组编号" -TimeoutSec $PromptTimeoutSeconds
  foreach ($tv in (Expand-IdTokens $line)) { [void]$selectedGroupIds.Add($tv) }
  if ($selectedGroupIds.Count -eq 0) {
    $choice = 3
  }
} elseif ($choice -eq 4) {
  $status = "regenerate_requested"
  $reviewFeedback = Read-BlockingReviewFeedback
  if ([string]::IsNullOrWhiteSpace($reviewFeedback)) {
    Write-Host "未输入反馈，已退回重新生成。" -ForegroundColor Yellow
  }
}

if ($choice -eq 1 -or $choice -eq 2) {
  $status = "completed"
  foreach ($group in @($commitGroups | Sort-Object GroupId)) {
    if (-not $selectedGroupIds.Contains([int]$group.GroupId)) {
      $group.Status = "skipped"
      continue
    }

    if ($errors.Count -gt 0) {
      $group.Status = "not_started"
      continue
    }

    Write-Host ""
    $kindLabel = if ($group.Source -eq "svn") { "SVN 提交组" } elseif ($group.Source -eq "git") { "GIT 仓库" } else { $group.Source.ToUpperInvariant() }
    Write-Host ("正在执行提交组 [{0}] {1} {2}" -f $group.GroupId, $kindLabel, $group.DisplayName) -ForegroundColor Cyan
    $group.Status = "running"
    $groupMessageFile = Join-Path $env:TEMP ("git_svn_commit_group_{0}_{1}.txt" -f $group.GroupId, ([guid]::NewGuid()))
    [System.IO.File]::WriteAllText($groupMessageFile, $group.CommitMessage.TrimEnd("`r", "`n"), [System.Text.UTF8Encoding]::new($false))
    try {
      if ($group.Source -eq "git") {
        $repoPaths = @($group.Paths | Where-Object { $_ } | Sort-Object -Unique)
        Write-Host "正在暂存 Git 文件..." -ForegroundColor Cyan
        $cmds = @(Invoke-GitCommandWithIndexLockRecovery -Arguments (@("add", "-A", "--") + $repoPaths) -WorkingDirectory $group.GitRepoRoot -StaleMinutes $GitIndexLockStaleMinutes)
        $commands += $cmds
        $group.Commands = @($group.Commands) + $cmds
        $cmd = $cmds[-1]
        if ($cmd.exitCode -ne 0) {
          $detail = (@($cmd.output) | Where-Object { $_ } | Select-Object -Last 1)
          if ($detail) { throw "git add 失败：$($group.DisplayName)。$detail" }
          throw "git add 失败：$($group.DisplayName)"
        }

        Write-Host "正在执行 git commit..." -ForegroundColor Cyan
        $cmds = @(Invoke-GitCommandWithIndexLockRecovery -Arguments (@("commit", "-F", $groupMessageFile, "--") + $repoPaths) -WorkingDirectory $group.GitRepoRoot -StaleMinutes $GitIndexLockStaleMinutes)
        $commands += $cmds
        $group.Commands = @($group.Commands) + $cmds
        $cmd = $cmds[-1]
        if ($cmd.exitCode -ne 0) {
          $detail = (@($cmd.output) | Where-Object { $_ } | Select-Object -Last 1)
          if ($detail) { throw "git commit 失败：$($group.DisplayName)。$detail" }
          throw "git commit 失败：$($group.DisplayName)"
        }

        Write-Host "正在执行 git push..." -ForegroundColor Cyan
        $pushResult = Invoke-GitPushWithUpstreamRetry -WorkingDirectory $group.GitRepoRoot -GitIndexLockStaleMinutes $GitIndexLockStaleMinutes
        $cmds = @($pushResult.Commands)
        $commands += $cmds
        $group.Commands = @($group.Commands) + $cmds
        $cmd = $pushResult.FinalCommand
        if ($cmd.exitCode -ne 0) {
          $detail = (@($cmd.output) | Where-Object { $_ } | Select-Object -Last 1)
          if ($detail) { throw "git push 失败：$($group.DisplayName)。$detail" }
          throw "git push 失败：$($group.DisplayName)"
        }
      } elseif ($group.Source -eq "svn") {
        if (-not $group.SvnWcRoot) { throw "SVN 工作副本根目录为空" }
        $svnPaths = @($group.Paths | Sort-Object -Unique)
        Write-Host ("正在执行 svn commit：{0}（{1} 个文件）" -f $group.SvnWcRoot, $svnPaths.Count) -ForegroundColor Cyan
        $cmd = Invoke-LoggedCommand -Tool "svn" -Arguments (@("commit", "-F", $groupMessageFile, "--") + $svnPaths) -WorkingDirectory $group.SvnWcRoot
        $commands += $cmd
        $group.Commands = @($group.Commands) + $cmd
        if ($cmd.exitCode -ne 0) { throw "svn commit 失败：$($group.DisplayName)" }
      }

      $group.Status = "completed"
    } catch {
      $message = $_.Exception.Message
      $group.Status = "failed"
      $group.Errors = @($group.Errors) + $message
      $errors += $message
      $status = "failed"
      Write-Host $message -ForegroundColor Red
    } finally {
      Remove-Item -LiteralPath $groupMessageFile -Force -ErrorAction SilentlyContinue
    }
  }

  if ($status -eq "failed") {
    foreach ($group in @($commitGroups | Where-Object { $_.Status -eq "not_started" })) {
      $kindLabel = if ($group.Source -eq "svn") { "SVN 提交组" } elseif ($group.Source -eq "git") { "GIT 仓库" } else { $group.Source.ToUpperInvariant() }
      Write-Host ("未执行提交组 [{0}] {1} {2}" -f $group.GroupId, $kindLabel, $group.DisplayName) -ForegroundColor Yellow
    }
    Write-Host "提交/推送失败，请查看输出。" -ForegroundColor Red
  } else {
    Write-Host "提交/推送完成。" -ForegroundColor Green
  }
} elseif ($choice -eq 4) {
  Write-Host "已记录修改意见，将退回模型重新生成提交日志。" -ForegroundColor Yellow
} else {
  Write-Host "已选择暂不提交。" -ForegroundColor Yellow
}

$result = [pscustomobject]@{
  Status = $status
  Choice = $choice
  GitPathCount = @($gitItems).Count
  SvnPathCount = $svnItems.Count
  Errors = @($errors)
  Commands = @($commands)
  CommitMessageSha256 = Get-Utf8Sha256 $messageText
  CommitMessage = $messageText
  ReviewFeedback = $reviewFeedback
  ReviewFeedbackRaw = $reviewFeedback
  Groups = @($commitGroups)
}

if ($ResultJsonFile) {
  $json = ConvertTo-ResultJson $result
  [System.IO.File]::WriteAllText($ResultJsonFile, $json, [System.Text.UTF8Encoding]::new($false))
} else {
  ConvertTo-ResultJson $result
}
