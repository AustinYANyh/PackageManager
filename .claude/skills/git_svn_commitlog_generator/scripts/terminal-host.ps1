function Quote-ForSingleQuotedPowerShell([string]$value) {
  return $value.Replace("'", "''")
}

function Get-PreferredPowerShellExecutable {
  $pwsh = Get-Command -Name "pwsh.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($pwsh) { return $pwsh.Source }

  $windowsPowerShell = Get-Command -Name "powershell.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($windowsPowerShell) { return $windowsPowerShell.Source }

  throw "找不到 pwsh.exe 或 powershell.exe，无法打开交互 PowerShell。"
}

function Get-WindowsTerminalExecutable {
  $wt = Get-Command -Name "wt.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($wt) { return $wt.Source }
  return ""
}

function Invoke-InteractivePowerShellScript {
  param(
    [Parameter(Mandatory = $true)][string]$ScriptText,
    [ValidateSet("Normal","Hidden","Minimized","Maximized")]
    [string]$WindowStyle = "Normal",
    [string]$Title = "Git/SVN Commit Helper",
    [string]$WorkingDirectory = "",
    [int]$StartupTimeoutSeconds = 15
  )

  $powerShellExe = Get-PreferredPowerShellExecutable
  $started = Join-Path $env:TEMP ("git_svn_terminal_started_{0}.txt" -f ([guid]::NewGuid()))
  $done = Join-Path $env:TEMP ("git_svn_terminal_done_{0}.txt" -f ([guid]::NewGuid()))
  $startedQ = Quote-ForSingleQuotedPowerShell $started
  $doneQ = Quote-ForSingleQuotedPowerShell $done

  $wrappedScript = @"
try {
  [System.IO.File]::WriteAllText('$startedQ', 'started', [System.Text.UTF8Encoding]::new(`$false))
} catch {
}
try {
$ScriptText
} finally {
  try {
    [System.IO.File]::WriteAllText('$doneQ', 'done', [System.Text.UTF8Encoding]::new(`$false))
  } catch {
  }
}
"@
  $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($wrappedScript))
  $psArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-EncodedCommand", $encodedCommand
  )

  try {
    $wtExe = Get-WindowsTerminalExecutable
    $canUseWindowsTerminal = ($WindowStyle -in @("Normal","Maximized") -and $wtExe)
    if ($canUseWindowsTerminal) {
      try {
        $wtArgs = @()
        if ($WindowStyle -eq "Maximized") { $wtArgs += "--maximized" }
        if ($Title) { $wtArgs += @("--title", $Title) }
        $wtArgs += @($powerShellExe)
        $wtArgs += $psArgs

        $startArgs = @{
          FilePath = $wtExe
          ArgumentList = $wtArgs
          PassThru = $true
        }
        if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }
        [void](Start-Process @startArgs)

        $startupDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $StartupTimeoutSeconds))
        while (-not (Test-Path -LiteralPath $started)) {
          if ([DateTime]::UtcNow -gt $startupDeadline) {
            throw "Windows Terminal 启动后未在 $StartupTimeoutSeconds 秒内进入 PowerShell。"
          }
          Start-Sleep -Milliseconds 200
        }

        while (-not (Test-Path -LiteralPath $done)) {
          Start-Sleep -Milliseconds 200
        }

        return [pscustomobject]@{
          ExitCode = 0
          Host = "WindowsTerminal"
          PowerShellPath = $powerShellExe
          TerminalPath = $wtExe
        }
      } catch {
        Remove-Item -LiteralPath $started -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $done -Force -ErrorAction SilentlyContinue
      }
    }

    $startArgs = @{
      FilePath = $powerShellExe
      WindowStyle = $WindowStyle
      Wait = $true
      PassThru = $true
      ArgumentList = $psArgs
    }
    if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }
    $proc = Start-Process @startArgs

    return [pscustomobject]@{
      ExitCode = $proc.ExitCode
      Host = "PowerShell"
      PowerShellPath = $powerShellExe
      TerminalPath = ""
    }
  } finally {
    Remove-Item -LiteralPath $started -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $done -Force -ErrorAction SilentlyContinue
  }
}
