param(
  [string]$Root = ".",
  [object]$IncludeUntracked = $true,
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

function To-Bool([object]$v, [bool]$defaultValue) {
  if ($null -eq $v) { return $defaultValue }
  if ($v -is [bool]) { return [bool]$v }
  if ($v -is [int]) { return ($v -ne 0) }
  $s = ($v.ToString()).Trim().ToLowerInvariant()
  if ($s -in @("1","true","t","yes","y","on")) { return $true }
  if ($s -in @("0","false","f","no","n","off")) { return $false }
  return $defaultValue
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

function Get-SvnStatusChanges([string]$wcRoot, [string]$rootFull) {
  $items = @()
  try {
    Push-Location -LiteralPath $wcRoot
    $xmlText = (& svn status --xml 2>$null) -join "`n"
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

      # item: modified, added, deleted, unversioned, replaced, conflicted, missing, etc.
      if ($item -eq "normal" -and $props -eq "none") { continue }

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
$IncludeUntracked = To-Bool -v $IncludeUntracked -defaultValue $true
$IncludeDiff = To-Bool -v $IncludeDiff -defaultValue $true
$Svn = To-Bool -v $Svn -defaultValue $true
$UseDefaultExcludes = To-Bool -v $UseDefaultExcludes -defaultValue $true
$hasGit = Test-IsGitRepo -rootFull $rootFull

$rawChanges = @()
if ($hasGit) {
  $rawChanges += Get-GitPorcelainChanges -rootFull $rootFull -includeUntracked $IncludeUntracked
}

if ($Svn) {
  $wcRoots = @()
  try { $wcRoots = Get-SvnWorkingCopyRoots -rootFull $rootFull } catch { $wcRoots = @() }
  foreach ($wc in $wcRoots) {
    $rawChanges += Get-SvnStatusChanges -wcRoot $wc -rootFull $rootFull
  }
}

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
$ordered = $rawChanges | Sort-Object Source, Path, IndexStatus, WorktreeStatus, SvnItem, WcRoot
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

function Is-GitUntracked($item) {
  return ($item.Source -eq "git" -and $item.GitIndexStatus -eq "?" -and $item.GitWorktreeStatus -eq "?")
}

function Is-SvnUnversioned($item) {
  return ($item.Source -eq "svn" -and ($item.SvnItem -eq "unversioned"))
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
  foreach ($it in $included) {
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

$out = [pscustomobject]@{
  Root = $rootFull
  GeneratedAt = (Get-Date).ToString("o")
  Defaults = [pscustomobject]@{
    UseDefaultExcludes = $UseDefaultExcludes
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
  ItemsIncluded = @($included)
  ItemsExcluded = @($excluded)
  Diffs = $diffById
  Projects = @($projects)
}

$out | ConvertTo-Json -Depth 10
