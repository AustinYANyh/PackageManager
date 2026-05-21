param(
  [string]$Root = ".",
  [Parameter(Mandatory = $true)][string]$ChangesJsonFile,
  [Parameter(Mandatory = $true)][string]$CommitMessageFile,
  [string]$CommitMessageGroupsJsonFile = "",
  [int]$PromptTimeoutSeconds = 30,
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
  return ('{{"Status":{0},"Choice":{1},"GitPathCount":{2},"SvnPathCount":{3},"Errors":{4},"Commands":{5},"CommitMessageSha256":{6},"CommitMessage":{7},"Groups":{8}}}' -f `
    (ConvertTo-JsonString $value.Status),
    ([int]$value.Choice),
    ([int]$value.GitPathCount),
    ([int]$value.SvnPathCount),
    $errorsJson,
    $commandsJson,
    (ConvertTo-JsonString $value.CommitMessageSha256),
    (ConvertTo-JsonString $value.CommitMessage),
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

  $resolvedTool = Get-Command -Name $Tool -CommandType Application -ErrorAction SilentlyContinue
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
  foreach ($key in $Environment.Keys) {
    $psi.EnvironmentVariables[$key] = [string]$Environment[$key]
  }

  $proc = New-Object System.Diagnostics.Process
  $proc.StartInfo = $psi
  try {
    [void]$proc.Start()
    if ($TimeoutSeconds -gt 0) {
      if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch {}
        Write-Host ("命令超时，已终止：{0}" -f $Tool) -ForegroundColor Red
        $exitCode = 124
      } else {
        $exitCode = $proc.ExitCode
      }
    } else {
      $proc.WaitForExit()
      $exitCode = $proc.ExitCode
    }
  } catch {
    $message = $_ | Out-String
    Write-Host $message -ForegroundColor Red
    $exitCode = 1
    if ($_.Exception -is [System.ComponentModel.Win32Exception] -and $_.Exception.NativeErrorCode -eq 2) {
      $exitCode = 127
    }
  }

  return [pscustomobject]@{
    tool = $Tool
    args = @($Arguments)
    exitCode = $exitCode
    output = @()
  }
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
    $buckets[$repoRoot].RepoPaths = @($buckets[$repoRoot].RepoPaths) + $repoPath
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
    $message = if ($messageByKey.ContainsKey($g.Key)) { $messageByKey[$g.Key] } else { $DefaultMessage }
    [void]$groups.Add([pscustomobject]@{
      GroupId = $id
      Source = "git"
      DisplayName = $g.DisplayName
      GitRepoRoot = $g.GitRepoRoot
      GitRepoRelRoot = $g.GitRepoRelRoot
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
    $message = if ($key -and $messageByKey.ContainsKey($key)) { $messageByKey[$key] } else { $DefaultMessage }
    [void]$groups.Add([pscustomobject]@{
      GroupId = $id
      Source = "svn"
      DisplayName = (Split-Path -Leaf $g.WcRoot)
      SvnWcRoot = $g.WcRoot
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

Write-Host ""
Write-Host "步骤 3/3：是否现在帮你提交并推送？" -ForegroundColor Cyan
Write-Host "操作说明：直接按 1 = 提交全部提交组（默认）；按 2 = 选择提交组；按 3 = 暂不提交。" -ForegroundColor Yellow
Write-Host "超时规则：${PromptTimeoutSeconds} 秒内不按键，自动选择 1，执行全部提交组。" -ForegroundColor Yellow
Write-Host ""
Write-Host ("本次包含 {0} 个提交组：Git 仓库 {1} 个，SVN 组 {2} 个，总文件 {3} 个。" -f @($commitGroups).Count, $gitGroupCount, $svnGroupCount, $totalFileCount) -ForegroundColor Cyan
foreach ($group in @($commitGroups | Sort-Object GroupId)) {
  Write-Host ""
  Write-Host ("[{0}] {1}  {2}  {3} 个文件" -f $group.GroupId, $group.Source.ToUpperInvariant(), $group.DisplayName, @($group.Items).Count) -ForegroundColor Cyan
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

$choice = Invoke-TimedChoiceKey -Choices "123" -TimeoutSec $PromptTimeoutSeconds -DefaultKey '1' -Message "请选择：1提交全部提交组 2选择提交组 3暂不提交"
$commands = @()
$status = "skipped"
$errors = @()
$selectedGroupIds = New-Object System.Collections.Generic.HashSet[int]

if ($choice -eq 1) {
  foreach ($group in @($commitGroups)) { [void]$selectedGroupIds.Add([int]$group.GroupId) }
} elseif ($choice -eq 2) {
  Write-Host "请输入要提交的组编号，多个编号用逗号/空格分隔，例如：1,3" -ForegroundColor Yellow
  $line = Read-TimedConsoleLine -Prompt "要提交的组编号" -TimeoutSec $PromptTimeoutSeconds
  foreach ($tok in ($line -split '[,\s;]+')) {
    $v = 0
    if ([int]::TryParse($tok, [ref]$v)) { [void]$selectedGroupIds.Add($v) }
  }
  if ($selectedGroupIds.Count -eq 0) {
    $choice = 3
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
    Write-Host ("正在执行提交组 [{0}] {1} {2}" -f $group.GroupId, $group.Source.ToUpperInvariant(), $group.DisplayName) -ForegroundColor Cyan
    $group.Status = "running"
    $groupMessageFile = Join-Path $env:TEMP ("git_svn_commit_group_{0}_{1}.txt" -f $group.GroupId, ([guid]::NewGuid()))
    [System.IO.File]::WriteAllText($groupMessageFile, $group.CommitMessage.TrimEnd("`r", "`n"), [System.Text.UTF8Encoding]::new($false))
    try {
      if ($group.Source -eq "git") {
        $repoPaths = @($group.Paths | Where-Object { $_ } | Sort-Object -Unique)
        Write-Host "正在暂存 Git 文件..." -ForegroundColor Cyan
        $cmd = Invoke-LoggedCommand -Tool "git" -Arguments (@("add", "-A", "--") + $repoPaths) -WorkingDirectory $group.GitRepoRoot
        $commands += $cmd
        $group.Commands = @($group.Commands) + $cmd
        if ($cmd.exitCode -ne 0) { throw "git add 失败：$($group.DisplayName)" }

        Write-Host "正在执行 git commit..." -ForegroundColor Cyan
        $cmd = Invoke-LoggedCommand -Tool "git" -Arguments (@("commit", "-F", $groupMessageFile, "--") + $repoPaths) -WorkingDirectory $group.GitRepoRoot
        $commands += $cmd
        $group.Commands = @($group.Commands) + $cmd
        if ($cmd.exitCode -ne 0) { throw "git commit 失败：$($group.DisplayName)" }

        Write-Host "正在执行 git push..." -ForegroundColor Cyan
        $cmd = Invoke-LoggedCommand -Tool "git" -Arguments @("-c", "credential.interactive=false", "push") -WorkingDirectory $group.GitRepoRoot -Environment @{ GIT_TERMINAL_PROMPT = "0" } -TimeoutSeconds 120
        $commands += $cmd
        $group.Commands = @($group.Commands) + $cmd
        if ($cmd.exitCode -ne 0) { throw "git push 失败：$($group.DisplayName)" }
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
      Write-Host ("未执行提交组 [{0}] {1} {2}" -f $group.GroupId, $group.Source.ToUpperInvariant(), $group.DisplayName) -ForegroundColor Yellow
    }
    Write-Host "提交/推送失败，请查看输出。" -ForegroundColor Red
  } else {
    Write-Host "提交/推送完成。" -ForegroundColor Green
  }
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
  Groups = @($commitGroups)
}

if ($ResultJsonFile) {
  $json = ConvertTo-ResultJson $result
  [System.IO.File]::WriteAllText($ResultJsonFile, $json, [System.Text.UTF8Encoding]::new($false))
} else {
  ConvertTo-ResultJson $result
}
