param(
  [string]$Root = ".",
  [Parameter(Mandatory = $true)][string]$ChangesJsonFile,
  [Parameter(Mandatory = $true)][string]$CommitMessageFile,
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

function ConvertTo-ResultJson([object]$value) {
  $errorsJson = ConvertTo-JsonStringArray @($value.Errors)
  $commandsJson = "[]"
  if ($value.Commands -and @($value.Commands).Count -gt 0) {
    $commandsJson = "[" + ((@($value.Commands) | ForEach-Object { ConvertTo-CommandJson $_ }) -join ",") + "]"
  }
  return ('{{"Status":{0},"Choice":{1},"GitPathCount":{2},"SvnPathCount":{3},"Errors":{4},"Commands":{5},"CommitMessageSha256":{6},"CommitMessage":{7}}}' -f `
    (ConvertTo-JsonString $value.Status),
    ([int]$value.Choice),
    ([int]$value.GitPathCount),
    ([int]$value.SvnPathCount),
    $errorsJson,
    $commandsJson,
    (ConvertTo-JsonString $value.CommitMessageSha256),
    (ConvertTo-JsonString $value.CommitMessage))
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

$rootFull = (Resolve-Path -LiteralPath $Root).Path
$changes = Get-Content -LiteralPath $ChangesJsonFile -Raw | ConvertFrom-Json
$messageText = Get-Content -LiteralPath $CommitMessageFile -Raw
$messageSha256 = Get-Utf8Sha256 $messageText
$items = @($changes.ItemsIncludedDefaultLog)
$gitPaths = @($items | Where-Object { $_.Source -eq "git" } | Select-Object -ExpandProperty Path)
$svnItems = @($items | Where-Object { $_.Source -eq "svn" })

Write-Host ""
Write-Host "步骤 3/3：是否现在帮你提交并推送？" -ForegroundColor Cyan
Write-Host "操作说明：直接按 1 = 提交并推送（默认）；按 2 = 暂不提交。这里按键立即生效，不需要回车。" -ForegroundColor Yellow
Write-Host "超时规则：${PromptTimeoutSeconds} 秒内不按键，自动选择 1，执行提交并推送。" -ForegroundColor Yellow
Write-Host ""
Write-Host "将使用以下提交日志：" -ForegroundColor Cyan
Write-Host $messageText
Write-Host ""
Write-Host ("提交日志 SHA256：{0}" -f $messageSha256) -ForegroundColor DarkGray
Write-Host ""
Write-Host ("Git 文件：{0} 个；SVN 文件：{1} 个。" -f $gitPaths.Count, $svnItems.Count) -ForegroundColor Cyan
foreach ($p in $gitPaths) { Write-Host ("  Git  {0}" -f $p) }
foreach ($s in $svnItems) { Write-Host ("  SVN  {0}" -f $s.Path) }
Write-Host ""

$choice = Invoke-TimedChoiceKey -Choices "12" -TimeoutSec $PromptTimeoutSeconds -DefaultKey '1' -Message "请选择：1提交并推送 2暂不提交"
$commands = @()
$status = "skipped"
$errors = @()

if ($choice -eq 1) {
  $status = "completed"
  if ($gitPaths.Count -gt 0) {
    Write-Host "正在暂存 Git 文件..." -ForegroundColor Cyan
    $commands += Invoke-LoggedCommand -Tool "git" -Arguments (@("add", "-A", "--") + $gitPaths) -WorkingDirectory $rootFull
    if ($commands[-1].exitCode -ne 0) { $errors += "git add 失败" }

    if ($errors.Count -eq 0) {
      Write-Host "正在执行 git commit..." -ForegroundColor Cyan
      $commands += Invoke-LoggedCommand -Tool "git" -Arguments (@("commit", "-F", $CommitMessageFile, "--") + $gitPaths) -WorkingDirectory $rootFull
      if ($commands[-1].exitCode -ne 0) { $errors += "git commit 失败" }
    }

    if ($errors.Count -eq 0) {
      Write-Host "正在执行 git push..." -ForegroundColor Cyan
      $commands += Invoke-LoggedCommand -Tool "git" -Arguments @("-c", "credential.interactive=false", "push") -WorkingDirectory $rootFull -Environment @{ GIT_TERMINAL_PROMPT = "0" } -TimeoutSeconds 120
      if ($commands[-1].exitCode -ne 0) { $errors += "git push 失败" }
    }
  }

  if ($svnItems.Count -gt 0 -and $errors.Count -eq 0) {
    $svnGroups = Get-SvnCommitGroups -Items $svnItems -RootFull $rootFull
    foreach ($group in $svnGroups) {
      $wcRoot = $group.WcRoot
      if (-not $wcRoot) {
        $errors += "SVN 工作副本根目录为空"
        continue
      }
      $svnPaths = @($group.Items | ForEach-Object {
        Join-Path -Path $rootFull -ChildPath ($_.Path -replace '/', '\')
      } | Sort-Object -Unique)
      Write-Host ("正在执行 svn commit：{0}（{1} 个文件）" -f $wcRoot, $svnPaths.Count) -ForegroundColor Cyan
      $commands += Invoke-LoggedCommand -Tool "svn" -Arguments (@("commit", "-F", $CommitMessageFile, "--") + $svnPaths) -WorkingDirectory $wcRoot
      if ($commands[-1].exitCode -ne 0) { $errors += "svn commit 失败：$wcRoot" }
    }
  }

  if ($errors.Count -gt 0) {
    $status = "failed"
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
  GitPathCount = $gitPaths.Count
  SvnPathCount = $svnItems.Count
  Errors = @($errors)
  Commands = @($commands)
  CommitMessageSha256 = $messageSha256
  CommitMessage = $messageText
}

if ($ResultJsonFile) {
  $json = ConvertTo-ResultJson $result
  [System.IO.File]::WriteAllText($ResultJsonFile, $json, [System.Text.UTF8Encoding]::new($false))
} else {
  ConvertTo-ResultJson $result
}
