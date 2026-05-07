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

function Normalize-RelPath([string]$fullPath, [string]$rootFull) {
  try {
    $rel = [System.IO.Path]::GetRelativePath($rootFull, $fullPath)
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

function Test-IsGitRepo([string]$rootFull) {
  try {
    Push-Location -LiteralPath $rootFull
    $v = (git rev-parse --is-inside-work-tree 2>$null).Trim()
    return ($v -eq "true")
  } catch { return $false } finally { Pop-Location -ErrorAction SilentlyContinue }
}

function Get-GitPorcelainChanges([string]$rootFull, [bool]$includeUntracked) {
  $items = @()
  try {
    Push-Location -LiteralPath $rootFull
    $args = @("status", "--porcelain=v1", "-z")
    if ($includeUntracked) {
      $args += "--untracked-files=all"
    } else {
      $args += "--untracked-files=no"
    }
    $raw = & git @args 2>$null
    if (-not $raw) { return @() }

    $tokens = $raw -split "`0" | Where-Object { $_ -ne "" }
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
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $items
}

function Get-GitDiffForPath([string]$rootFull, [string]$path, [bool]$cached, [int]$maxBytes) {
  try {
    Push-Location -LiteralPath $rootFull
    $args = @("diff")
    if ($cached) { $args += "--cached" }
    $args += @("--", $path)
    $txt = (& git @args 2>$null) -join "`n"
    if (-not $txt) { return "" }
    return (Safe-TrimBytes -text $txt -maxBytes $maxBytes)
  } catch { return "" } finally { Pop-Location -ErrorAction SilentlyContinue }
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

function Get-SvnStatusChanges([string]$wcRoot, [string]$rootFull, [bool]$quiet) {
  $items = @()
  try {
    Push-Location -LiteralPath $wcRoot
    $svnArgs = @("status", "--xml")
    if ($quiet) { $svnArgs += "-q" }
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
      $rel = Normalize-RelPath -fullPath $abs -rootFull $rootFull

      # item: modified, added, deleted, unversioned, replaced, conflicted, missing, ignored, etc.
      if ($item -eq "normal" -and $props -eq "none") { continue }
      if ($item -eq "ignored") { continue }

      $items += [pscustomobject]@{
        Source = "svn"
        Path = $rel
        WcRoot = $wcRoot
        SvnItem = $item
      }
    }
  } catch {
    return @()
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $items
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

# 已跟踪的待提交：Git（无 ??）+ SVN（status --xml -q，不含未版本管理，避免海量条目）
$rawTracked = @()
if ($hasGit) {
  $rawTracked += Get-GitPorcelainChanges -rootFull $rootFull -includeUntracked $false
}

$wcRoots = @()
if ($Svn) {
  try { $wcRoots = Get-SvnWorkingCopyRoots -rootFull $rootFull } catch { $wcRoots = @() }
  foreach ($wc in $wcRoots) {
    $rawTracked += Get-SvnStatusChanges -wcRoot $wc -rootFull $rootFull -quiet $true
  }
}

$rawTracked = Merge-DuplicateSourcesByPath -rawChanges $rawTracked

$trackedPathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($t in $rawTracked) {
  if ($t.Path) { [void]$trackedPathSet.Add(($t.Path -replace '\\', '/')) }
}

$rawUntracked = @()
if ($ScanUntrackedForNeedsAdd) {
  if ($hasGit) {
    $gitAll = Get-GitPorcelainChanges -rootFull $rootFull -includeUntracked $true
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
  }
  $id += 1
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
    Write-Host "操作说明：直接按 1 = 不加入（默认）；按 2 = 全部加入；按 3 = 输入编号选择加入。这里按键立即生效，不需要回车。" -ForegroundColor Yellow
    Write-Host "超时规则：${PromptTimeoutSeconds} 秒内不按键，自动选择 1，不加入任何未跟踪文件。" -ForegroundColor Yellow
    Write-Host "文件列表：" -ForegroundColor Cyan
    foreach ($x in $naList) { Write-Host ("  编号 {0,-6} 来源 {1,-4} 路径 {2}" -f $x.Id, $x.Source, $x.Path) }
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

  Write-Host ""
  $excludePromptList = @(
    $items | Where-Object {
      ((Is-TrackedPendingChange $_) -and -not (Is-ExcludedItem -item $_ -excludeIdSet $excludeIdSet -excludePathSet $excludePathSet)) -or
      ($addAllCandidates -and (((Is-GitUntracked $_) -or (Is-SvnUnversioned $_)) -and (Is-CommonAddCandidate $_))) -or
      ($addIdSet.Contains([int]$_.Id))
    } | Sort-Object Id
  )

  Write-Host ("步骤 2/2：以下是会进入本次提交日志的改动项（共 {0} 项），请确认是否要排除某些项：" -f $excludePromptList.Count) -ForegroundColor Cyan
  Write-Host "操作说明：直接按 1 = 全部保留（默认）；按 2 = 输入编号排除。这里按键立即生效，不需要回车。" -ForegroundColor Yellow
  Write-Host "超时规则：${PromptTimeoutSeconds} 秒内不按键，自动选择 1，不排除任何文件。" -ForegroundColor Yellow
  Write-Host "说明：未在步骤 1 选择加入的未跟踪文件不会列在这里，也不会进入提交日志。" -ForegroundColor Yellow
  Write-Host "文件列表（如果这里缺少你认为应提交的文件，请先关闭窗口并重新运行 skill，确保文件已保存且 Git 状态已刷新）：" -ForegroundColor Cyan
  foreach ($x in $excludePromptList) { Write-Host ("  编号 {0,-6} 路径 {1}" -f $x.Id, $x.Path) }
  Write-Host ""
  if ($excludePromptList.Count -eq 0) {
    Write-Host "当前没有可排除的提交日志改动项，跳过排除选择。" -ForegroundColor Yellow
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

# Attach diffs (limited)
$diffCount = 0
$diffById = @{}
if ($IncludeDiff) {
  $includedForDiff = @(
    $included | Sort-Object @{ Expression = { if ((Is-GitUntracked $_) -or (Is-SvnUnversioned $_)) { 1 } else { 0 } }; Ascending = $true }, Id
  )
  foreach ($it in $includedForDiff) {
    if ($diffCount -ge $MaxFilesWithDiff) { break }
    if ($it.Source -eq "git" -and $hasGit) {
      $unstaged = Get-GitDiffForPath -rootFull $rootFull -path $it.Path -cached:$false -maxBytes $MaxDiffBytesPerFile
      $staged = Get-GitDiffForPath -rootFull $rootFull -path $it.Path -cached:$true -maxBytes $MaxDiffBytesPerFile
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

$addedIdSet = New-Object System.Collections.Generic.HashSet[int]
foreach ($a in $addActions) {
  if ($null -ne $a.id) { [void]$addedIdSet.Add([int]$a.id) }
}
$includedDefaultLog = @(
  $included | Where-Object { (Is-TrackedPendingChange $_) -or $addedIdSet.Contains([int]$_.Id) } | Sort-Object Id
)
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
