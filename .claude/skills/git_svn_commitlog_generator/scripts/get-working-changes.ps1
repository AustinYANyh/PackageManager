param(
  [string]$Root = ".",
  # 为 NeedsAdd 扫描 Git ?? / SVN unversioned；关闭可加速超大混合工作副本（默认开启）
  [object]$ScanUntrackedForNeedsAdd = $true,
  # 脚本直接运行时默认无人值守；skill 默认通过可见 PowerShell 传 -Interactive 做限时交互
  [switch]$NonInteractive,
  [switch]$Interactive,
  [int]$PromptTimeoutSeconds = 30,
  [object]$IncludeDiff = $true,
  [int]$MaxDiffBytesPerFile = 40960,
  [int]$MaxFilesWithDiff = 80,
  [object]$Svn = $true,
  [object]$UseDefaultExcludes = $true,
  [string[]]$ExcludeIds = @(),
  [string[]]$ExcludePaths = @(),
  [string[]]$AddIds = @()
)

$ErrorActionPreference = "Stop"
try {
  [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
  $OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
}

function To-Bool([object]$v, [bool]$defaultValue) {
  if ($null -eq $v) { return $defaultValue }
  if ($v -is [bool]) { return [bool]$v }
  if ($v -is [System.Management.Automation.SwitchParameter]) { return [bool]$v }
  if ($v -is [int]) { return ($v -ne 0) }
  $s = ($v.ToString()).Trim().ToLowerInvariant()
  if ($s -in @("1","true","t","yes","y","on")) { return $true }
  if ($s -in @("0","false","f","no","n","off")) { return $false }
  return $defaultValue
}

function Test-WindowsConsoleChoice {
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
  if (-not (Test-WindowsConsoleChoice)) { return 1 }
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
  if (-not (Test-WindowsConsoleChoice)) { return "" }
  if ($TimeoutSec -lt 1) { $TimeoutSec = 1 }

  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
  $chars = New-Object System.Collections.Generic.List[char]
  $lastLen = 0

  function Render-TimedInputLine {
    param(
      [string]$PromptText,
      [int]$RemainingSeconds,
      [string]$CurrentText,
      [int]$PreviousLength
    )
    $line = ("{0}（输入编号后按回车；剩余 {1} 秒，超时=不选择）： {2}" -f $PromptText, $RemainingSeconds, $CurrentText)
    $pad = if ($PreviousLength -gt $line.Length) { " " * ($PreviousLength - $line.Length) } else { "" }
    Write-Host -NoNewline ("`r{0}{1}" -f $line, $pad)
    return $line.Length
  }

  while ([DateTime]::UtcNow -lt $deadline) {
    $remaining = [Math]::Max(0, [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds))
    $lastLen = Render-TimedInputLine -PromptText $Prompt -RemainingSeconds $remaining -CurrentText (-join $chars) -PreviousLength $lastLen

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
      if ($chars.Count -gt 0) {
        $chars.RemoveAt($chars.Count - 1)
      }
      continue
    }

    if (-not [char]::IsControl($key.KeyChar)) {
      $chars.Add($key.KeyChar)
    }
  }

  Write-Host ""
  return -join $chars
}

function Get-ItemPropText([object]$item, [string]$name) {
  if ($null -eq $item) { return "" }
  $prop = $item.PSObject.Properties[$name]
  if ($null -eq $prop -or $null -eq $prop.Value) { return "" }
  return [string]$prop.Value
}

function Get-ItemStatusText([object]$item) {
  $source = Get-ItemPropText -item $item -name "Source"
  if ($source -eq "git") {
    $ix = Get-ItemPropText -item $item -name "GitIndexStatus"
    $wt = Get-ItemPropText -item $item -name "GitWorktreeStatus"
    $s = ("{0}{1}" -f $ix, $wt).Trim()
    if ($s) { return $s }
  }
  $svn = Get-ItemPropText -item $item -name "SvnItem"
  if ($svn) { return $svn }
  return ""
}

function Write-ItemTable {
  param(
    [object[]]$Items,
    [string]$CheckHeader = ""
  )
  if ($null -eq $Items -or $Items.Count -eq 0) { return }
  $rows = @($Items | ForEach-Object {
    [pscustomobject]@{
      Id = Get-ItemPropText -item $_ -name "Id"
      Mark = $CheckHeader
      Source = Get-ItemPropText -item $_ -name "Source"
      Status = Get-ItemStatusText $_
      Project = Get-ItemPropText -item $_ -name "Project"
      Path = Get-ItemPropText -item $_ -name "Path"
    }
  })
  $rows | Format-Table -AutoSize | Out-String -Width 4096 | Write-Host
}

function Invoke-ItemCheckDialog {
  param(
    [object[]]$Items,
    [string]$Title,
    [string]$Instruction,
    [string]$CheckHeader,
    [bool]$DefaultChecked,
    [int]$TimeoutSec = 30
  )

  $result = [pscustomobject]@{
    Used = $false
    TimedOut = $false
    CheckedIds = @()
  }

  if ($null -eq $Items -or $Items.Count -eq 0) { return $result }
  if ($TimeoutSec -lt 1) { $TimeoutSec = 1 }

  try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
  } catch {
    return $result
  }

  $form = New-Object System.Windows.Forms.Form
  $form.Text = $Title
  $form.StartPosition = "CenterScreen"
  $workingArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $formWidth = [Math]::Min(1120, [Math]::Max(900, $workingArea.Width - 80))
  $formHeight = [Math]::Min(680, [Math]::Max(520, $workingArea.Height - 80))
  $form.Size = New-Object System.Drawing.Size($formWidth, $formHeight)
  $form.MinimumSize = New-Object System.Drawing.Size(900, 520)
  $form.TopMost = $true

  $layout = New-Object System.Windows.Forms.TableLayoutPanel
  $layout.Dock = "Fill"
  $layout.ColumnCount = 1
  $layout.RowCount = 3
  [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Absolute, 58)))
  [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Absolute, 50)))
  [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))
  $form.Controls.Add($layout)

  $label = New-Object System.Windows.Forms.Label
  $label.AutoSize = $false
  $label.Dock = "Fill"
  $label.Padding = New-Object System.Windows.Forms.Padding(10, 8, 10, 4)
  $label.Text = $Instruction
  $layout.Controls.Add($label, 0, 0)

  $panel = New-Object System.Windows.Forms.FlowLayoutPanel
  $panel.Dock = "Fill"
  $panel.FlowDirection = "LeftToRight"
  $panel.WrapContents = $false
  $panel.Padding = New-Object System.Windows.Forms.Padding(10, 8, 10, 6)
  $layout.Controls.Add($panel, 0, 1)

  $grid = New-Object System.Windows.Forms.DataGridView
  $grid.Dock = "Fill"
  $grid.AllowUserToAddRows = $false
  $grid.AllowUserToDeleteRows = $false
  $grid.RowHeadersVisible = $false
  $grid.SelectionMode = "FullRowSelect"
  $grid.MultiSelect = $true
  $grid.AutoGenerateColumns = $false
  $grid.AutoSizeRowsMode = "AllCells"
  $grid.DefaultCellStyle.WrapMode = [System.Windows.Forms.DataGridViewTriState]::True
  $grid.EditMode = "EditOnEnter"

  $colCheck = New-Object System.Windows.Forms.DataGridViewCheckBoxColumn
  $colCheck.Name = "Checked"
  $colCheck.HeaderText = $CheckHeader
  $colCheck.Width = 58
  [void]$grid.Columns.Add($colCheck)

  foreach ($spec in @(
    @("Id","编号",64),
    @("Source","来源",64),
    @("Status","状态",72),
    @("Project","项目",160),
    @("Path","路径",700)
  )) {
    $col = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $col.Name = $spec[0]
    $col.HeaderText = $spec[1]
    $col.Width = [int]$spec[2]
    if ($spec[0] -eq "Path") { $col.AutoSizeMode = "Fill" }
    $col.ReadOnly = $true
    [void]$grid.Columns.Add($col)
  }

  foreach ($item in $Items) {
    $idx = $grid.Rows.Add()
    $row = $grid.Rows[$idx]
    $row.Cells["Checked"].Value = $DefaultChecked
    $row.Cells["Id"].Value = Get-ItemPropText -item $item -name "Id"
    $row.Cells["Source"].Value = Get-ItemPropText -item $item -name "Source"
    $row.Cells["Status"].Value = Get-ItemStatusText $item
    $row.Cells["Project"].Value = Get-ItemPropText -item $item -name "Project"
    $row.Cells["Path"].Value = Get-ItemPropText -item $item -name "Path"
  }

  $state = [pscustomobject]@{
    Remaining = $TimeoutSec
  }

  $getCheckedCount = {
    $checked = 0
    foreach ($r in $grid.Rows) {
      if ([bool]$r.Cells["Checked"].Value) { $checked++ }
    }
    return $checked
  }

  $updateCheckedCount = {
    $checked = & $getCheckedCount
    $timeoutText = if ($checked -gt 0) { "超时进入下一步并使用当前勾选" } else { "超时进入下一步并使用默认" }
    $countLabel.Text = ("已勾选 {0}/{1}；剩余 {2} 秒，{3}" -f $checked, $grid.Rows.Count, ([Math]::Max(0, $state.Remaining)), $timeoutText)
  }

  $grid.add_CurrentCellDirtyStateChanged({
    if ($grid.IsCurrentCellDirty) {
      [void]$grid.CommitEdit([System.Windows.Forms.DataGridViewDataErrorContexts]::Commit)
    }
  })
  $grid.add_CellValueChanged({
    param($sender, $eventArgs)
    if ($eventArgs.RowIndex -ge 0 -and $eventArgs.ColumnIndex -ge 0 -and $grid.Columns[$eventArgs.ColumnIndex].Name -eq "Checked") {
      & $updateCheckedCount
    }
  })
  $grid.add_CellClick({
    param($sender, $eventArgs)
    if ($eventArgs.RowIndex -lt 0 -or $eventArgs.ColumnIndex -lt 0) { return }
    if ($grid.Columns[$eventArgs.ColumnIndex].Name -eq "Checked") { return }
    $cell = $grid.Rows[$eventArgs.RowIndex].Cells["Checked"]
    $cell.Value = -not [bool]$cell.Value
    & $updateCheckedCount
  })

  $btnAll = New-Object System.Windows.Forms.Button
  $btnAll.Text = "全选"
  $btnAll.Width = 80
  $btnAll.Height = 30
  $btnAll.Add_Click({ foreach ($r in $grid.Rows) { $r.Cells["Checked"].Value = $true }; & $updateCheckedCount })
  $panel.Controls.Add($btnAll)

  $btnNone = New-Object System.Windows.Forms.Button
  $btnNone.Text = "全不选"
  $btnNone.Width = 80
  $btnNone.Height = 30
  $btnNone.Add_Click({ foreach ($r in $grid.Rows) { $r.Cells["Checked"].Value = $false }; & $updateCheckedCount })
  $panel.Controls.Add($btnNone)

  $btnInvert = New-Object System.Windows.Forms.Button
  $btnInvert.Text = "反选"
  $btnInvert.Width = 80
  $btnInvert.Height = 30
  $btnInvert.Add_Click({ foreach ($r in $grid.Rows) { $r.Cells["Checked"].Value = -not [bool]$r.Cells["Checked"].Value }; & $updateCheckedCount })
  $panel.Controls.Add($btnInvert)

  $btnOk = New-Object System.Windows.Forms.Button
  $btnOk.Text = "确定，进入下一步"
  $btnOk.Width = 140
  $btnOk.Height = 30
  $btnOk.Add_Click({ $form.Tag = "ok"; $form.Close() })
  $panel.Controls.Add($btnOk)

  $btnDefault = New-Object System.Windows.Forms.Button
  $btnDefault.Text = "使用默认"
  $btnDefault.Width = 90
  $btnDefault.Height = 30
  $btnDefault.Add_Click({ $form.Tag = "default"; $form.Close() })
  $panel.Controls.Add($btnDefault)

  $countLabel = New-Object System.Windows.Forms.Label
  $countLabel.AutoSize = $true
  $countLabel.Margin = New-Object System.Windows.Forms.Padding(12, 7, 0, 0)
  $panel.Controls.Add($countLabel)

  $form.AcceptButton = $btnOk
  $form.CancelButton = $btnDefault
  $layout.Controls.Add($grid, 0, 2)

  $timer = New-Object System.Windows.Forms.Timer
  $timer.Interval = 1000
  $timer.Add_Tick({
    $state.Remaining--
    & $updateCheckedCount
    if ($state.Remaining -le 0) {
      $timer.Stop()
      $form.Tag = if ((& $getCheckedCount) -gt 0) { "ok" } else { "timeout" }
      $form.Close()
    }
  })
  & $updateCheckedCount
  $form.Add_Shown({ $timer.Start(); $form.Activate() })
  $form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })

  [void]$form.ShowDialog()

  $result.Used = $true
  if ($form.Tag -eq "timeout") { $result.TimedOut = $true }

  if ($form.Tag -eq "ok") {
    $ids = @()
    foreach ($r in $grid.Rows) {
      if ([bool]$r.Cells["Checked"].Value) {
        $v = 0
        if ([int]::TryParse([string]$r.Cells["Id"].Value, [ref]$v)) { $ids += $v }
      }
    }
    $result.CheckedIds = @($ids)
  } else {
    if ($DefaultChecked) {
      $result.CheckedIds = @($Items | ForEach-Object { [int](Get-ItemPropText -item $_ -name "Id") })
    } else {
      $result.CheckedIds = @()
    }
  }

  return $result
}

function Normalize-RelPath([string]$fullPath, [string]$rootFull) {
  try {
    $rootResolved = [System.IO.Path]::GetFullPath($rootFull)
    $pathResolved = [System.IO.Path]::GetFullPath($fullPath)
    if (-not $rootResolved.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
      $rootResolved += [System.IO.Path]::DirectorySeparatorChar
    }
    $rootUri = New-Object System.Uri($rootResolved)
    $pathUri = New-Object System.Uri($pathResolved)
    $rel = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
    return $rel -replace '\\','/'
  } catch {
    return $fullPath -replace '\\','/'
  }
}

function Is-SkippedDirName([string]$name) {
  $skip = @(".git",".idea",".vscode",".vs",".dotnet","node_modules",".nuget","bin","obj","packages","dist","out",".build")
  return $skip -contains $name
}

function Should-ExcludeByDefault([string]$relPath) {
  if (-not $relPath) { return $false }
  $p = $relPath.Trim() -replace '\\','/'

  # IDE / tooling folders (any depth)
  if ($p -match '(^|/)(\.vs|\.idea|\.vscode|\.kiro|\.dotnet)(/|$)') { return $true }

  # Common generated / dependency / artifact dirs
  if ($p -match '(^|/)(bin|obj|node_modules|packages|artifacts|\.build)(/|$)') { return $true }

  # Patch files & temporary docs
  if ($p.ToLowerInvariant().EndsWith(".patch")) { return $true }
  if ($p -match '(^|/)Temp(/|$)') { return $true }
  if ($p -match '(^|/)(gitlog\.md)$') { return $true }

  return $false
}

function Safe-TrimBytes([string]$text, [int]$maxBytes) {
  if (-not $text) { return "" }
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
  if ($bytes.Length -le $maxBytes) { return $text }
  $slice = $bytes[0..($maxBytes-1)]
  return [System.Text.Encoding]::UTF8.GetString($slice)
}

function Quote-NativeArgument([string]$value) {
  if ($null -eq $value) { return '""' }
  if ($value -eq "") { return '""' }
  if ($value -notmatch '[\s"`]') { return $value }
  return '"' + (($value -replace '\\(?=")', '$0$0') -replace '"', '\"') + '"'
}

function Invoke-NativeText {
  param(
    [string]$Tool,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [int]$TimeoutSeconds = 15
  )

  try {
    $cmd = Get-Command -Name $Tool -CommandType Application -ErrorAction SilentlyContinue
    if (-not $cmd) { return "" }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cmd.Source
    $psi.Arguments = (@($Arguments) | ForEach-Object { Quote-NativeArgument $_ }) -join " "
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    if ($TimeoutSeconds -gt 0) {
      if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch {}
        return ""
      }
    } else {
      $proc.WaitForExit()
    }
    $stdout = $stdoutTask.Result
    [void]$stderrTask.Result
    if ($proc.ExitCode -ne 0) { return "" }
    return $stdout
  } catch {
    return ""
  }
}

function Test-IsGitRepo([string]$rootFull) {
  try {
    Push-Location -LiteralPath $rootFull
    $v = (git rev-parse --is-inside-work-tree 2>$null).Trim()
    return ($v -eq "true")
  } catch { return $false } finally { Pop-Location -ErrorAction SilentlyContinue }
}

function Get-GitRepoRoot([string]$rootFull) {
  try {
    Push-Location -LiteralPath $rootFull
    $v = (git rev-parse --show-toplevel 2>$null).Trim()
    if ($v) { return (Resolve-Path -LiteralPath $v).Path }
  } catch {
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $rootFull
}

function Get-GitPorcelainChanges([string]$rootFull, [bool]$includeUntracked) {
  $items = @()
  try {
    $gitArgs = @("-c", "status.relativePaths=false", "status", "--porcelain=v1", "-z")
    if ($includeUntracked) {
      $gitArgs += "--untracked-files=all"
    } else {
      $gitArgs += "--untracked-files=no"
    }

    # Use ProcessStartInfo for porcelain -z output. Native redirection with
    # NUL-delimited stdout can terminate early in some Windows PowerShell hosts.
    $raw = Invoke-NativeText -Tool "git" -Arguments $gitArgs -WorkingDirectory $rootFull
    if (-not $raw) {
      Push-Location -LiteralPath $rootFull
      try {
        $raw = & git @gitArgs
      } finally {
        Pop-Location -ErrorAction SilentlyContinue
      }
    }
    if (-not $raw) { return @() }

    if ($raw -is [array]) {
      $raw = [string]::Join("`0", @($raw))
    }

    $tokens = @($raw -split "`0" | Where-Object { $_ -ne "" })
    $i = 0
    while ($i -lt $tokens.Count) {
      $t = $tokens[$i]
      if ($t.Length -lt 4) { $i++; continue }
      $xy = $t.Substring(0,2)
      $path1 = $t.Substring(3)

      $x = $xy.Substring(0,1)
      $y = $xy.Substring(1,1)

      if (-not $includeUntracked -and $x -eq "?" -and $y -eq "?") {
        $i++; continue
      }

      $renamedFrom = $null
      $path = $path1

      # rename/copy in porcelain -z emits: "R100 old\0new\0" or "C100 old\0new\0"
      if ($x -match '^[RC]$' -or $y -match '^[RC]$' -or $xy -match '^[RC]') {
        if (($i + 1) -lt $tokens.Count) {
          $renamedFrom = $path1
          $path = $tokens[$i + 1]
          $i += 1
        }
      }

      $items += [pscustomobject]@{
        Source = "git"
        Path = $path -replace '\\','/'
        IndexStatus = $x
        WorktreeStatus = $y
        RenamedFrom = if ($renamedFrom) { $renamedFrom -replace '\\','/' } else { "" }
      }
      $i += 1
    }
  } catch {
    return @()
  }
  return $items
}

function Get-GitDiffForPath([string]$rootFull, [string]$path, [bool]$cached, [int]$maxBytes) {
  $args = @("diff", "--no-ext-diff", "--no-color")
  if ($cached) { $args += "--cached" }
  $args += @("--", $path)

  $nativeText = Invoke-NativeText -Tool "git" -Arguments $args -WorkingDirectory $rootFull
  if ($nativeText) { return (Safe-TrimBytes -text $nativeText -maxBytes $maxBytes) }

  try {
    Push-Location -LiteralPath $rootFull
    $txt = (& git @args 2>$null) -join "`n"
    if ($txt) { return (Safe-TrimBytes -text $txt -maxBytes $maxBytes) }
  } catch {
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }

  try {
    $txt = (& git -C $rootFull @args 2>$null) -join "`n"
    if ($txt) { return (Safe-TrimBytes -text $txt -maxBytes $maxBytes) }
  } catch {
  }

  try {
    $txt = (& git -C $rootFull @args 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0 -and $txt) { return (Safe-TrimBytes -text $txt -maxBytes $maxBytes) }
  } catch {
  }

  return ""
}

function Get-SvnWorkingCopyRoots([string]$rootFull) {
  $roots = New-Object System.Collections.Generic.HashSet[string]
  $queue = New-Object System.Collections.Generic.Queue[string]
  $queue.Enqueue($rootFull)

  while ($queue.Count -gt 0) {
    $dir = $queue.Dequeue()
    $svnDir = Join-Path -Path $dir -ChildPath ".svn"
    if (Test-Path -LiteralPath $svnDir) {
      try {
        Push-Location -LiteralPath $dir
        $wcRoot = (svn info --show-item wc-root 2>$null).Trim()
        if ($wcRoot) { [void]$roots.Add($wcRoot) } else { [void]$roots.Add($dir) }
      } catch {
        [void]$roots.Add($dir)
      } finally {
        Pop-Location -ErrorAction SilentlyContinue
      }
      continue
    }

    $children = @()
    try {
      $children = Get-ChildItem -LiteralPath $dir -Force -Directory -ErrorAction SilentlyContinue
    } catch { $children = @() }

    foreach ($c in $children) {
      if (Is-SkippedDirName $c.Name) { continue }
      $queue.Enqueue($c.FullName)
    }
  }

  return @($roots)
}

function Get-SvnStatusChanges([string]$wcRoot, [string]$rootFull, [bool]$quiet, [bool]$includeNormal = $false) {
  $items = @()
  try {
    Push-Location -LiteralPath $wcRoot
    $svnArgs = @("status", "--xml")
    if ($includeNormal) {
      $svnArgs += "-v"
    } elseif ($quiet) {
      $svnArgs += "-q"
    }
    $xmlText = (& svn @svnArgs 2>$null) -join "`n"
    if (-not $xmlText) { return @() }
    $xml = [xml]$xmlText
    $entries = $xml.status.target.entry
    foreach ($e in $entries) {
      $p = $e.path
      $item = $e.'wc-status'.item
      $props = $e.'wc-status'.props
      $rel = $p
      $abs = $p
      if (-not [System.IO.Path]::IsPathRooted($p)) {
        $abs = Join-Path -Path $wcRoot -ChildPath $p
      }
      if ((Normalize-RelPath -fullPath $abs -rootFull $rootFull) -notmatch '^\.\.') {
        $rel = Normalize-RelPath -fullPath $abs -rootFull $rootFull
      } else {
        $rel = Normalize-RelPath -fullPath $abs -rootFull $wcRoot
      }

      # item: modified, added, deleted, unversioned, replaced, conflicted, missing, ignored, etc.
      if ($item -eq "ignored") { continue }
      if (-not $includeNormal -and $item -eq "normal" -and $props -eq "none") { continue }

      $items += [pscustomobject]@{
        Source = "svn"
        Path = $rel
        WcRoot = $wcRoot
        SvnItem = $item
        SvnProps = $props
      }
    }
  } catch {
    return @()
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $items
}

function Get-SvnStatusText([object]$value) {
  if ($null -eq $value) { return "" }
  return ([string]$value).Trim().ToLowerInvariant()
}

function Test-SvnVersionedStatus([object]$item) {
  $s = Get-SvnStatusText $item
  return ($s -notin @("","unversioned","ignored","external"))
}

function Test-SvnPendingStatus([object]$item, [object]$props) {
  $itemStatus = Get-SvnStatusText $item
  $propStatus = Get-SvnStatusText $props

  if ($itemStatus -in @("","normal","none","unversioned","ignored","external")) {
    return ($propStatus -notin @("","normal","none"))
  }

  return $true
}

function Get-SvnInfoForPath([string]$wcRoot, [string]$relPathFromRoot, [string]$rootFull) {
  $abs = Join-Path -Path $rootFull -ChildPath ($relPathFromRoot -replace '/','\')

  try {
    Push-Location -LiteralPath $wcRoot
    $xmlText = (& svn info --xml -- $abs 2>$null) -join "`n"
    if (-not $xmlText) { return $null }
    $xml = [xml]$xmlText
    $entry = $xml.info.entry | Select-Object -First 1
    if (-not $entry) { return $null }

    return [pscustomobject]@{
      WcRoot = [string]$entry.'wc-info'.'wcroot-abspath'
      RepoRootUrl = [string]$entry.repository.root
      RepoUuid = [string]$entry.repository.uuid
    }
  } catch {
    return $null
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
}

# 同一路径同时出现在 Git 与 SVN 时保留一条，优先 Git。
function Merge-DuplicateSourcesByPath([System.Collections.IEnumerable]$rawChanges) {
  $list = @($rawChanges)
  if ($list.Count -le 1) { return $list }
  $byPath = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::OrdinalIgnoreCase)
  foreach ($c in $list) {
    if (-not $c -or -not $c.Path) { continue }
    $p = [string]($c.Path -replace '\\', '/')
    if (-not $byPath.ContainsKey($p)) {
      $byPath[$p] = $c
      continue
    }
    $existing = $byPath[$p]
    if ($existing.Source -eq "svn" -and $c.Source -eq "git") {
      $byPath[$p] = $c
    }
  }
  return @($byPath.Values)
}

function Get-SvnDiffForPath([string]$wcRoot, [string]$relPathFromRoot, [string]$rootFull, [int]$maxBytes) {
  try {
    $abs = Join-Path -Path $rootFull -ChildPath ($relPathFromRoot -replace '/','\')
    if (-not (Test-Path -LiteralPath $abs)) {
      # deleted files might not exist; try diff by relative path in wc
      $abs = Join-Path -Path $wcRoot -ChildPath ($relPathFromRoot -replace '/','\')
    }
    $nativeText = Invoke-NativeText -Tool "svn" -Arguments @("diff", "--", $abs) -WorkingDirectory $wcRoot
    if ($nativeText) { return (Safe-TrimBytes -text $nativeText -maxBytes $maxBytes) }

    Push-Location -LiteralPath $wcRoot
    $txt = (& svn diff -- $abs 2>$null) -join "`n"
    if (-not $txt) { return "" }
    return (Safe-TrimBytes -text $txt -maxBytes $maxBytes)
  } catch { return "" } finally { Pop-Location -ErrorAction SilentlyContinue }
}

function Find-NearestCsproj([string]$fileAbsPath) {
  $dir = Split-Path -Parent $fileAbsPath
  while ($dir -and (Test-Path -LiteralPath $dir)) {
    $csproj = Get-ChildItem -LiteralPath $dir -Filter "*.csproj" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($csproj) { return $csproj.FullName }
    $parent = Split-Path -Parent $dir
    if ($parent -eq $dir) { break }
    $dir = $parent
  }
  return ""
}

function Get-ProjectNameForPath([string]$relPath, [string]$rootFull, $cache) {
  if (-not $relPath) { return "" }
  if ($cache.ContainsKey($relPath)) { return $cache[$relPath] }

  $abs = Join-Path -Path $rootFull -ChildPath ($relPath -replace '/','\')
  $proj = ""
  if (Test-Path -LiteralPath $abs) {
    $csproj = Find-NearestCsproj -fileAbsPath $abs
    if ($csproj) {
      $proj = [System.IO.Path]::GetFileNameWithoutExtension($csproj)
    }
  }
  if (-not $proj) {
    $parts = $relPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Length -gt 0) { $proj = $parts[0] }
  }
  $cache[$relPath] = $proj
  return $proj
}

$rootFull = (Resolve-Path -LiteralPath $Root).Path
$ScanUntrackedForNeedsAdd = To-Bool -v $ScanUntrackedForNeedsAdd -defaultValue $true
$nonInteractiveMode = $true
if ($Interactive.IsPresent) { $nonInteractiveMode = $false }
if ($NonInteractive.IsPresent) { $nonInteractiveMode = $true }
if ($PromptTimeoutSeconds -lt 1) { $PromptTimeoutSeconds = 30 }
$IncludeDiff = To-Bool -v $IncludeDiff -defaultValue $true
$Svn = To-Bool -v $Svn -defaultValue $true
$UseDefaultExcludes = To-Bool -v $UseDefaultExcludes -defaultValue $true
$hasGit = Test-IsGitRepo -rootFull $rootFull
$gitRepoRoot = $rootFull
if ($hasGit) {
  $gitRepoRoot = Get-GitRepoRoot -rootFull $rootFull
}
if ($hasGit) {
  try {
    Push-Location -LiteralPath $gitRepoRoot
    & git update-index -q --refresh 2>$null
  } catch {
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
}

# 已跟踪的待提交：Git（无 ??）+ SVN（status --xml -q，不含未版本管理，避免海量条目）
$rawTracked = @()
$trackedPathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
if ($hasGit) {
  $gitTracked = Get-GitPorcelainChanges -rootFull $gitRepoRoot -includeUntracked $false
  $rawTracked += $gitTracked
  foreach ($g in $gitTracked) {
    if ($g.Path) { [void]$trackedPathSet.Add(($g.Path -replace '\\', '/')) }
  }
}

$wcRoots = @()
if ($Svn) {
  try { $wcRoots = Get-SvnWorkingCopyRoots -rootFull $rootFull } catch { $wcRoots = @() }
  foreach ($wc in $wcRoots) {
    $svnEntries = Get-SvnStatusChanges -wcRoot $wc -rootFull $rootFull -quiet $false -includeNormal $true
    foreach ($s in $svnEntries) {
      if (-not $s.Path) { continue }
      $pn = ($s.Path -replace '\\', '/')
      if (Test-SvnVersionedStatus $s.SvnItem -and -not $trackedPathSet.Contains($pn)) {
        [void]$trackedPathSet.Add($pn)
      }
      if (-not (Test-SvnPendingStatus -item $s.SvnItem -props $s.SvnProps)) { continue }
      $rawTracked += $s
    }
  }
}

$rawTracked = Merge-DuplicateSourcesByPath -rawChanges $rawTracked

$rawUntracked = @()
if ($ScanUntrackedForNeedsAdd) {
  if ($hasGit) {
    $gitAll = Get-GitPorcelainChanges -rootFull $gitRepoRoot -includeUntracked $true
    foreach ($g in $gitAll) {
      if ($g.IndexStatus -ne "?" -or $g.WorktreeStatus -ne "?") { continue }
      $pn = ($g.Path -replace '\\', '/')
      if (-not $trackedPathSet.Contains($pn)) { $rawUntracked += $g }
    }
  }
  if ($Svn) {
    foreach ($wc in $wcRoots) {
      $svnFull = Get-SvnStatusChanges -wcRoot $wc -rootFull $rootFull -quiet $false
      foreach ($s in $svnFull) {
        if ($s.SvnItem -ne "unversioned") { continue }
        $pn = ($s.Path -replace '\\', '/')
        if (-not $trackedPathSet.Contains($pn)) { $rawUntracked += $s }
      }
    }
  }
  $rawUntracked = Merge-DuplicateSourcesByPath -rawChanges $rawUntracked
}

$orderedTracked = @($rawTracked | Sort-Object Source, Path, IndexStatus, WorktreeStatus, SvnItem, WcRoot)
$orderedUntracked = @($rawUntracked | Sort-Object Source, Path, IndexStatus, WorktreeStatus, SvnItem, WcRoot)
$rawChanges = @($orderedTracked) + @($orderedUntracked)

# Normalize exclusion inputs
$excludeIdSet = New-Object System.Collections.Generic.HashSet[int]
foreach ($id in $ExcludeIds) {
  $v = 0
  if ([int]::TryParse($id, [ref]$v)) { [void]$excludeIdSet.Add($v) }
}
$excludePathSet = New-Object System.Collections.Generic.HashSet[string]
foreach ($p in $ExcludePaths) {
  if (-not $p) { continue }
  $norm = $p.Trim() -replace '\\','/'
  [void]$excludePathSet.Add($norm)
}

# Assign stable ids by (Source, Path, ...), then filter by id/path
# 保持「已跟踪变更」在前、「未跟踪候选」在后，便于默认提交日志与稳定 Id
$ordered = $rawChanges
$projectCache = @{}
$items = @()
$id = 1
foreach ($c in $ordered) {
  $path = $c.Path
  if (-not $path) { continue }

  if ($UseDefaultExcludes -and (Should-ExcludeByDefault -relPath $path)) { continue }

  $proj = Get-ProjectNameForPath -relPath $path -rootFull $rootFull -cache $projectCache

  $items += [pscustomobject]@{
    Id = $id
    Source = $c.Source
    Path = $path
    Project = $proj
    GitIndexStatus = if ($c.PSObject.Properties.Match("IndexStatus").Count) { $c.IndexStatus } else { "" }
    GitWorktreeStatus = if ($c.PSObject.Properties.Match("WorktreeStatus").Count) { $c.WorktreeStatus } else { "" }
    GitRenamedFrom = if ($c.PSObject.Properties.Match("RenamedFrom").Count) { $c.RenamedFrom } else { "" }
    SvnItem = if ($c.PSObject.Properties.Match("SvnItem").Count) { $c.SvnItem } else { "" }
    SvnWcRoot = if ($c.PSObject.Properties.Match("WcRoot").Count) { $c.WcRoot } else { "" }
    SvnRepoRootUrl = ""
    SvnRepoUuid = ""
  }
  $id += 1
}

$svnInfoCache = @{}
foreach ($item in @($items | Where-Object { $_.Source -eq "svn" })) {
  if (-not $item.SvnWcRoot) { continue }
  $cacheKey = "$($item.SvnWcRoot)|$($item.Path)"
  if (-not $svnInfoCache.ContainsKey($cacheKey)) {
    $svnInfoCache[$cacheKey] = Get-SvnInfoForPath -wcRoot $item.SvnWcRoot -relPathFromRoot $item.Path -rootFull $rootFull
  }
  $info = $svnInfoCache[$cacheKey]
  if (-not $info) { continue }
  if ($info.WcRoot) { $item.SvnWcRoot = $info.WcRoot }
  if ($info.RepoRootUrl) { $item.SvnRepoRootUrl = $info.RepoRootUrl }
  if ($info.RepoUuid) { $item.SvnRepoUuid = $info.RepoUuid }
}

function Is-ExcludedItem($item, $excludeIdSet, $excludePathSet) {
  if ($excludeIdSet.Contains([int]$item.Id)) { return $true }
  $p = $item.Path
  foreach ($ex in $excludePathSet) {
    if (-not $ex) { continue }
    if ($p -eq $ex) { return $true }
    if ($ex.EndsWith("/")) {
      if ($p.StartsWith($ex)) { return $true }
    } else {
      # treat as prefix if ends with wildcard-like slash missing? keep exact unless caller adds /
      continue
    }
  }
  return $false
}

function Is-GitUntracked($item) {
  return ($item.Source -eq "git" -and $item.GitIndexStatus -eq "?" -and $item.GitWorktreeStatus -eq "?")
}

function Is-SvnUnversioned($item) {
  return ($item.Source -eq "svn" -and ($item.SvnItem -eq "unversioned"))
}

function Is-TrackedPendingChange($item) {
  return -not ((Is-GitUntracked $item) -or (Is-SvnUnversioned $item))
}

function Is-CommonAddCandidate($item) {
  if (-not $item -or -not $item.Path) { return $false }

  $path = $item.Path -replace '\\','/'
  if ($path.EndsWith("/")) { return $false }

  $lower = $path.ToLowerInvariant()
  $ext = [System.IO.Path]::GetExtension($lower)
  $name = [System.IO.Path]::GetFileName($lower)

  $codeExts = @(
    ".cs",".vb",".fs",".fsx",
    ".c",".h",".cpp",".hpp",".cc",".cxx",".hh",".hxx",".ixx",".inl",".cu",".cuh",
    ".m",".mm",".swift",
    ".ts",".tsx",".js",".jsx",".mjs",".cjs",".mts",".cts",
    ".py",".pyw",".java",".kt",".kts",".go",".rs",".php",".rb",
    ".scala",".sc",".groovy",".dart",".lua",".r",".jl",".ex",".exs",
    ".erl",".hrl",".clj",".cljs",".fsproj",
    ".xaml",".axaml",".cshtml",".razor",".sql",".plsql",
    ".glsl",".hlsl",".shader",".compute",".wgsl"
  )
  $webExts = @(
    ".html",".htm",".css",".scss",".sass",".less",".styl",
    ".vue",".svelte",".astro",".ejs",".hbs",".handlebars",".liquid",
    ".twig",".jinja",".j2",".jsp",".aspx",".ascx"
  )
  $scriptExts = @(
    ".ps1",".psm1",".psd1",".bat",".cmd",".sh",".bash",".zsh",
    ".fish",".psql",".sqlcmd",".awk",".sed"
  )
  $configExts = @(
    ".json",".jsonc",".yml",".yaml",".xml",".config",".props",".targets",
    ".csproj",".vbproj",".vcxproj",".vcxproj.filters",".sqlproj",".shproj",".sln",".slnx",
    ".proj",".xproj",".nuspec",".toml",".ini",".env",".properties",".gradle",".cmake",
    ".tf",".tfvars",".hcl",".bicep",".editorconfig",
    ".gitattributes",".gitignore",".dockerignore",".npmrc",".yarnrc"
  )
  $docExts = @(
    ".md",".markdown",".mdx",".rst",".adoc"
  )
  $schemaAndDataExts = @(
    ".proto",".graphql",".graphqls",".gql",".thrift",".avsc",".xsd",".wsdl",
    ".resx",".rc",".manifest",".reg",".plist"
  )

  if ($codeExts -contains $ext) { return $true }
  if ($webExts -contains $ext) { return $true }
  if ($scriptExts -contains $ext) { return $true }
  if ($configExts -contains $ext) { return $true }
  if ($docExts -contains $ext) { return $true }
  if ($schemaAndDataExts -contains $ext) { return $true }

  $commonConfigNames = @(
    "directory.build.props","directory.build.targets","nuget.config",
    "packages.config","app.config","web.config","global.json","dockerfile",
    "makefile","cmakelists.txt","package.json","package-lock.json",
    "pnpm-lock.yaml","yarn.lock","vite.config.ts","vite.config.js",
    "webpack.config.js","rollup.config.js","eslint.config.js",
    ".gitignore",".gitattributes",".dockerignore",".editorconfig",
    ".env",".env.example",".env.local"
  )
  if ($commonConfigNames -contains $name) { return $true }

  return $false
}

function Add-ToVersionControl($item, [string]$rootFull) {
  if (Is-GitUntracked $item) {
    try {
      Push-Location -LiteralPath $rootFull
      & git add -- $item.Path 2>$null | Out-Null
    } finally {
      Pop-Location -ErrorAction SilentlyContinue
    }
    return
  }

  if (Is-SvnUnversioned $item) {
    $abs = Join-Path -Path $rootFull -ChildPath ($item.Path -replace '/','\')
    $wc = $item.SvnWcRoot
    if (-not $wc) { return }
    try {
      Push-Location -LiteralPath $wc
      & svn add --parents -- $abs 2>$null | Out-Null
    } finally {
      Pop-Location -ErrorAction SilentlyContinue
    }
    return
  }
}

$addIdSet = New-Object System.Collections.Generic.HashSet[int]
$addAllCandidates = $false
foreach ($aid in $AddIds) {
  if ($aid -and $aid.Trim().ToLowerInvariant() -eq "all") {
    $addAllCandidates = $true
    continue
  }
  $v = 0
  if ([int]::TryParse($aid, [ref]$v)) { [void]$addIdSet.Add($v) }
}

$consoleChoiceUsed = $false
if (-not $nonInteractiveMode -and (Test-WindowsConsoleChoice)) {
  $naList = @($items | Where-Object { ((Is-GitUntracked $_) -or (Is-SvnUnversioned $_)) -and (Is-CommonAddCandidate $_) } | Sort-Object Id)
  if ($naList.Count -gt 0) {
    Write-Host ""
    Write-Host "步骤 1/2：发现以下文件还没有纳入版本管理，可能需要加入本次提交：" -ForegroundColor Cyan
    Write-Host "点击窗口中要加入的文件；点“确定”或超时都会进入下一步；未操作超时/使用默认 = 不加入任何未跟踪文件。" -ForegroundColor Yellow
    Write-Host "文件列表：" -ForegroundColor Cyan
    Write-ItemTable -Items $naList
    $dlgAdd = Invoke-ItemCheckDialog -Items $naList -Title "步骤 1/2：选择要加入版本管理的文件" -Instruction "勾选要加入本次提交的未跟踪文件。点“确定”或超时都会进入下一步；未操作超时/点击“使用默认”表示不加入任何文件。" -CheckHeader "加入" -DefaultChecked $false -TimeoutSec $PromptTimeoutSeconds
    if ($dlgAdd.Used) {
      $consoleChoiceUsed = $true
      foreach ($id in @($dlgAdd.CheckedIds)) { [void]$addIdSet.Add([int]$id) }
    } else {
      Write-Host ""
      $ch = Invoke-TimedChoiceKey -Choices "123" -TimeoutSec $PromptTimeoutSeconds -DefaultKey '1' -Message "请选择：1不加入 2全部加入 3输入编号"
      $consoleChoiceUsed = $true
      if ($ch -eq 2) { $addAllCandidates = $true }
      elseif ($ch -eq 3) {
        Write-Host "请输入要加入的编号，多个编号用逗号/空格分隔，例如：3,5,8" -ForegroundColor Yellow
        $ln = Read-TimedConsoleLine -Prompt "要加入的编号" -TimeoutSec $PromptTimeoutSeconds
        foreach ($tok in ($ln -split '[,\s;]+')) { $tv = 0; if ([int]::TryParse($tok, [ref]$tv)) { [void]$addIdSet.Add($tv) } }
      }
    }
  }

  Write-Host ""
  $excludePromptList = @(
    $items | Where-Object {
      ((Is-TrackedPendingChange $_) -and -not (Is-ExcludedItem -item $_ -excludeIdSet $excludeIdSet -excludePathSet $excludePathSet)) -or
      ($addAllCandidates -and (((Is-GitUntracked $_) -or (Is-SvnUnversioned $_)) -and (Is-CommonAddCandidate $_))) -or
      ($addIdSet.Contains([int]$_.Id))
    } | Sort-Object Id
  )

  Write-Host ("步骤 2/2：以下是会进入本次提交日志的改动项（共 {0} 项），请确认是否要排除某些项：" -f $excludePromptList.Count) -ForegroundColor Cyan
  Write-Host "默认会打开勾选表格：勾选 = 排除；未勾选 = 保留。点击任意文件行可切换勾选。" -ForegroundColor Yellow
  Write-Host "点“确定”或超时都会进入下一步；未操作超时/点击“使用默认” = 全部保留，不排除任何文件。" -ForegroundColor Yellow
  Write-Host "如果 GUI 不可用，会回退为编号输入：直接按 1 = 全部保留（默认）；按 2 = 输入编号排除。" -ForegroundColor Yellow
  Write-Host "说明：未在步骤 1 选择加入的未跟踪文件不会列在这里，也不会进入提交日志。" -ForegroundColor Yellow
  Write-Host "文件列表（如果这里缺少你认为应提交的文件，请先关闭窗口并重新运行 skill，确保文件已保存且 Git 状态已刷新）：" -ForegroundColor Cyan
  Write-ItemTable -Items $excludePromptList -CheckHeader "排除"
  Write-Host ""
  if ($excludePromptList.Count -eq 0) {
    Write-Host "当前没有可排除的提交日志改动项，跳过排除选择。" -ForegroundColor Yellow
  } else {
    $dlgExclude = Invoke-ItemCheckDialog -Items $excludePromptList -Title "步骤 2/2：选择要排除的文件" -Instruction "勾选要从本次提交日志中排除的文件；未勾选表示保留。点“确定”或超时都会进入下一步；未操作超时/点击“使用默认”表示全部保留。" -CheckHeader "排除" -DefaultChecked $false -TimeoutSec $PromptTimeoutSeconds
    if ($dlgExclude.Used) {
      $consoleChoiceUsed = $true
      foreach ($id in @($dlgExclude.CheckedIds)) { [void]$excludeIdSet.Add([int]$id) }
    } else {
      $ch2 = Invoke-TimedChoiceKey -Choices "12" -TimeoutSec $PromptTimeoutSeconds -DefaultKey '1' -Message "请选择：1全部保留 2输入编号排除"
      $consoleChoiceUsed = $true
      if ($ch2 -eq 2) {
        Write-Host "请输入要排除的编号，多个编号用逗号/空格分隔，例如：3,5,8" -ForegroundColor Yellow
        $ln2 = Read-TimedConsoleLine -Prompt "要排除的编号" -TimeoutSec $PromptTimeoutSeconds
        foreach ($tok in ($ln2 -split '[,\s;]+')) { $tv = 0; if ([int]::TryParse($tok, [ref]$tv)) { [void]$excludeIdSet.Add($tv) } }
      }
    }
  }
}

$addActions = @()
foreach ($it in $items) {
  if ($addAllCandidates -or $addIdSet.Contains([int]$it.Id)) {
    $kind = if (((Is-GitUntracked $it) -or (Is-SvnUnversioned $it)) -and (Is-CommonAddCandidate $it)) {
      if (Is-GitUntracked $it) { "git-add" } else { "svn-add" }
    } else { "" }
    if ($kind) {
      Add-ToVersionControl -item $it -rootFull $rootFull
      $addActions += [pscustomobject]@{ id = $it.Id; action = $kind; path = $it.Path }
    }
  }
}

$excluded = @()
$included = @()
foreach ($it in $items) {
  if (Is-ExcludedItem -item $it -excludeIdSet $excludeIdSet -excludePathSet $excludePathSet) { $excluded += $it } else { $included += $it }
}

$needsAdd = @($included | Where-Object { ((Is-GitUntracked $_) -or (Is-SvnUnversioned $_)) -and (Is-CommonAddCandidate $_) } | Sort-Object Id)

$addedIdSet = New-Object System.Collections.Generic.HashSet[int]
foreach ($a in $addActions) {
  if ($null -ne $a.id) { [void]$addedIdSet.Add([int]$a.id) }
}
$includedDefaultLog = @(
  $included | Where-Object { (Is-TrackedPendingChange $_) -or $addedIdSet.Contains([int]$_.Id) } | Sort-Object Id
)

# Attach diffs (limited)
$diffCount = 0
$diffById = @{}
if ($IncludeDiff) {
  $includedForDiff = @(
    $includedDefaultLog | Sort-Object Id
  )
  foreach ($it in $includedForDiff) {
    if ($diffCount -ge $MaxFilesWithDiff) { break }
    if ($it.Source -eq "git" -and $hasGit) {
      $unstaged = Get-GitDiffForPath -rootFull $gitRepoRoot -path $it.Path -cached:$false -maxBytes $MaxDiffBytesPerFile
      $staged = Get-GitDiffForPath -rootFull $gitRepoRoot -path $it.Path -cached:$true -maxBytes $MaxDiffBytesPerFile
      $diffById["$($it.Id)"] = [pscustomobject]@{ unstaged = $unstaged; staged = $staged }
      $diffCount += 1
    } elseif ($it.Source -eq "svn" -and $Svn) {
      $wc = $it.SvnWcRoot
      if ($wc) {
        $d = Get-SvnDiffForPath -wcRoot $wc -relPathFromRoot $it.Path -rootFull $rootFull -maxBytes $MaxDiffBytesPerFile
        $diffById["$($it.Id)"] = [pscustomobject]@{ patch = $d }
        $diffCount += 1
      }
    }
  }
}

# Projects aggregation
$projGroups = $included | Group-Object Project | Sort-Object Name
$projects = @()
foreach ($g in $projGroups) {
  $name = $g.Name
  if (-not $name) { $name = "unknown" }
  $projects += [pscustomobject]@{
    Name = $name
    Scope = $name
    Items = @($g.Group | Sort-Object Id)
    Files = @($g.Group | Select-Object -ExpandProperty Path | Sort-Object -Unique)
    GitFiles = @($g.Group | Where-Object { $_.Source -eq "git" } | Select-Object -ExpandProperty Path | Sort-Object -Unique)
    SvnFiles = @($g.Group | Where-Object { $_.Source -eq "svn" } | Select-Object -ExpandProperty Path | Sort-Object -Unique)
  }
}

$projGroupsDefault = $includedDefaultLog | Group-Object Project | Sort-Object Name
$projectsDefault = @()
foreach ($g in $projGroupsDefault) {
  $name = $g.Name
  if (-not $name) { $name = "unknown" }
  $projectsDefault += [pscustomobject]@{
    Name = $name
    Scope = $name
    Items = @($g.Group | Sort-Object Id)
    Files = @($g.Group | Select-Object -ExpandProperty Path | Sort-Object -Unique)
    GitFiles = @($g.Group | Where-Object { $_.Source -eq "git" } | Select-Object -ExpandProperty Path | Sort-Object -Unique)
    SvnFiles = @($g.Group | Where-Object { $_.Source -eq "svn" } | Select-Object -ExpandProperty Path | Sort-Object -Unique)
  }
}

$out = [pscustomobject]@{
  Root = $rootFull
  GeneratedAt = (Get-Date).ToString("o")
  Defaults = [pscustomobject]@{
    UseDefaultExcludes = $UseDefaultExcludes
    ScanUntrackedForNeedsAdd = $ScanUntrackedForNeedsAdd
    IncludeDiff = $IncludeDiff
    MaxDiffBytesPerFile = $MaxDiffBytesPerFile
    MaxFilesWithDiff = $MaxFilesWithDiff
    NonInteractive = $nonInteractiveMode
    PromptTimeoutSeconds = $PromptTimeoutSeconds
    ConsoleChoiceUsed = $consoleChoiceUsed
  }
  Add = [pscustomobject]@{
    AddIds = @($AddIds)
    Actions = @($addActions)
  }
  Exclude = [pscustomobject]@{
    ExcludeIds = @($ExcludeIds)
    ExcludePaths = @($ExcludePaths)
  }
  Git = [pscustomobject]@{
    HasRepo = $hasGit
    RepoRoot = $gitRepoRoot
    ItemCount = @($items | Where-Object { $_.Source -eq "git" }).Count
  }
  Svn = [pscustomobject]@{
    Enabled = $Svn
    ItemCount = @($items | Where-Object { $_.Source -eq "svn" }).Count
  }
  NeedsAdd = @($needsAdd)
  ItemsAll = @($items)
  # 已纳入版本库且有改动的项（提交日志主线只用这些 + 对应 Diffs）
  ItemsIncludedDefaultLog = @($includedDefaultLog)
  ProjectsDefault = @($projectsDefault)
  ItemsIncluded = @($included)
  ItemsExcluded = @($excluded)
  Diffs = $diffById
  Projects = @($projects)
}

$out | ConvertTo-Json -Depth 10
