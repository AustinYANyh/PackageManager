param(
  [string]$Root = ".",
  [Parameter(Mandatory = $true)][string]$ChangesJsonFile,
  [Parameter(Mandatory = $true)][string]$CommitMessageFile,
  [int]$PromptTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
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

function Invoke-LoggedCommand {
  param(
    [string]$Tool,
    [string[]]$Arguments,
    [string]$WorkingDirectory
  )
  try {
    Push-Location -LiteralPath $WorkingDirectory
    $output = & $Tool @Arguments 2>&1
    $exitCode = $LASTEXITCODE
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return [pscustomobject]@{
    tool = $Tool
    args = @($Arguments)
    exitCode = $exitCode
    output = @($output | ForEach-Object { $_.ToString() })
  }
}

$rootFull = (Resolve-Path -LiteralPath $Root).Path
$changes = Get-Content -LiteralPath $ChangesJsonFile -Raw | ConvertFrom-Json
$messageText = Get-Content -LiteralPath $CommitMessageFile -Raw
$env:GIT_TERMINAL_PROMPT = "0"
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
      $commands += Invoke-LoggedCommand -Tool "git" -Arguments @("commit", "-F", $CommitMessageFile) -WorkingDirectory $rootFull
      if ($commands[-1].exitCode -ne 0) { $errors += "git commit 失败" }
    }

    if ($errors.Count -eq 0) {
      Write-Host "正在执行 git push..." -ForegroundColor Cyan
      $commands += Invoke-LoggedCommand -Tool "git" -Arguments @("-c", "credential.interactive=false", "push") -WorkingDirectory $rootFull
      if ($commands[-1].exitCode -ne 0) { $errors += "git push 失败" }
    }
  }

  if ($svnItems.Count -gt 0 -and $errors.Count -eq 0) {
    $svnGroups = $svnItems | Group-Object SvnWcRoot
    foreach ($group in $svnGroups) {
      $wcRoot = $group.Name
      if (-not $wcRoot) {
        $errors += "SVN 工作副本根目录为空"
        continue
      }
      $svnPaths = @($group.Group | ForEach-Object {
        Join-Path -Path $rootFull -ChildPath ($_.Path -replace '/', '\')
      })
      Write-Host ("正在执行 svn commit：{0}" -f $wcRoot) -ForegroundColor Cyan
      $commands += Invoke-LoggedCommand -Tool "svn" -Arguments (@("commit", "--non-interactive", "-F", $CommitMessageFile, "--") + $svnPaths) -WorkingDirectory $wcRoot
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

[pscustomobject]@{
  Status = $status
  Choice = $choice
  GitPathCount = $gitPaths.Count
  SvnPathCount = $svnItems.Count
  Errors = @($errors)
  Commands = @($commands)
} | ConvertTo-Json -Depth 8
