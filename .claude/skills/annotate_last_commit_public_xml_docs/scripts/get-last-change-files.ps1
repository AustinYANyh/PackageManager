param(
  # 如果只提供一个用户名，则同时用于 Git 和 SVN 的作者匹配
  [string]$Username,
  [string]$GitAuthor,
  [string]$SvnAuthor,
  [switch]$GitOnly,
  [switch]$SvnOnly,
  # 扫描嵌套 svn 工作副本的最大深度（从当前目录开始）
  [int]$SvnSearchDepth = 10,
  # svn log 取最近多少条记录用于匹配 author
  [int]$SvnLogHistory = 200,
  # 为 true（默认）：仅采纳“提交日期（本地日历日）= 脚本运行当日”的 SVN 提交；更早的作者提交整批 .cs 排除，并写入 SvnExcluded
  # 关闭：powershell ... -SvnTodayOnly:$false 或 -SvnAllDates
  [bool]$SvnTodayOnly = $true,
  # 与 -SvnTodayOnly:$false 等价：不按本地当日过滤 SVN
  [switch]$SvnAllDates
)

$ErrorActionPreference = "Stop"

if ($SvnAllDates) {
  $SvnTodayOnly = $false
}

if ([string]::IsNullOrWhiteSpace($GitAuthor)) {
  if (-not [string]::IsNullOrWhiteSpace($Username)) { $GitAuthor = $Username }
  else { $GitAuthor = "AustinYanyh" }
}

if ([string]::IsNullOrWhiteSpace($SvnAuthor)) {
  if (-not [string]::IsNullOrWhiteSpace($Username)) { $SvnAuthor = $Username }
  else { $SvnAuthor = "yanyunhao" }
}

function Is-CSharpFile([string]$path) {
  if (-not $path) { return $false }
  $lower = $path.ToLowerInvariant()
  if (-not $lower.EndsWith(".cs")) { return $false }
  if ($lower.EndsWith(".g.cs")) { return $false }
  if ($lower.EndsWith(".generated.cs")) { return $false }
  if ($lower.EndsWith(".designer.cs")) { return $false }
  if ($lower.EndsWith("assemblyinfo.cs")) { return $false }
  return $true
}

function Get-GitLastChangedCsFiles {
  param([string]$author)
  $files = @()
  try {
    $sha = (git log -1 --author=$author --pretty=format:%H 2>$null).Trim()
    if (-not $sha) { return @() }

    $raw = git show $sha --name-only --diff-filter=ACMRT 2>$null
    foreach ($line in $raw) {
      $p = $line.Trim()
      if (Is-CSharpFile $p) { $files += $p }
    }
  } catch {
    # ignore (not a git repo or git unavailable)
  }
  return $files | Sort-Object -Unique
}

function Get-SvnFirstLogEntryByAuthorInWorkingCopy {
  param(
    [string]$wcRoot,
    [string]$author
  )
  $result = $null
  try {
    Push-Location -LiteralPath $wcRoot

    # svn log 默认按从新到旧输出，因此取第一个匹配即可
    # 必须显式从 HEAD 开始；否则上限会被工作副本的当前 revision 截断
    $raw = svn log -l $SvnLogHistory -r HEAD:0 2>$null
    # r112538 | yanyunhao | 2026-04-02 14:54:26 +0800 (...) | N lines
    $pattern = '^r(\d+)\s+\|\s*([^|]+)\s*\|\s*([^|]+)\|'
    foreach ($line in ($raw -split "`r?`n")) {
      if ($line -match $pattern) {
        $candidateRev = [int]$Matches[1]
        $candidateAuthor = ($Matches[2]).Trim()
        $dateField = ($Matches[3]).Trim()
        if ($candidateAuthor -ieq $author.Trim()) {
          $commitDay = $null
          if ($dateField -match '(\d{4}-\d{2}-\d{2})') {
            try {
              $commitDay = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture).Date
            } catch {
              $commitDay = $null
            }
          }
          $result = [pscustomobject]@{
            Rev = $candidateRev
            Author = $candidateAuthor
            DateField = $dateField
            CommitDay = $commitDay
            RawLine = $line.Trim()
          }
          break
        }
      }
    }
  } catch {
    # ignore (not an svn working copy or svn unavailable)
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $result
}

function Get-SvnChangedCsFilesForRevisionInWorkingCopy {
  param(
    [string]$wcRoot,
    [int]$rev
  )
  $files = @()
  try {
    Push-Location -LiteralPath $wcRoot
    $raw = svn diff -c $rev --summarize 2>$null
    foreach ($line in $raw) {
      # format: "M       path/to/file.cs"
      $rel = ($line -replace '^\s*[A-Z]\s+','').Trim()
      if (-not $rel) { continue }
      if (Is-CSharpFile $rel) {
        $abs = $rel
        if (-not [System.IO.Path]::IsPathRooted($rel)) {
          $abs = (Join-Path -Path $wcRoot -ChildPath $rel)
        }
        $files += $abs
      }
    }
  } catch {
    # ignore
  } finally {
    Pop-Location -ErrorAction SilentlyContinue
  }
  return $files | Sort-Object -Unique
}

function Get-SvnWorkingCopyRoots {
  param(
    [string]$startPath,
    [int]$maxDepth
  )
  $roots = New-Object System.Collections.Generic.HashSet[string]
  $skipNames = @(
    ".git", ".idea", ".vscode", ".dotnet", "node_modules", ".nuget",
    "bin", "obj", "packages", "dist", "out"
  )

  function Recurse([string]$p, [int]$d) {
    if ($d -gt $maxDepth) { return }

    $svnDir = Join-Path -Path $p -ChildPath ".svn"
    if (Test-Path -LiteralPath $svnDir) {
      [void]$roots.Add($p)
      return
    }

    $children = @()
    try {
      $children = Get-ChildItem -LiteralPath $p -Force -Directory -ErrorAction SilentlyContinue
    } catch {
      return
    }

    foreach ($c in $children) {
      if ($skipNames -contains $c.Name) { continue }
      Recurse -p $c.FullName -d ($d + 1)
    }
  }

  Recurse -p $startPath -d 0
  return @($roots)
}

function Get-SvnLastChangedCsFiles {
  $files = @()
  $svnMeta = @()
  $svnExcluded = @()
  $todayLocal = [datetime]::Today

  # 如果当前目录不一定是 svn 工作副本根，就要扫描嵌套工作副本
  $startPath = (Get-Location).Path
  $wcRoots = Get-SvnWorkingCopyRoots -startPath $startPath -maxDepth $SvnSearchDepth
  if (-not $wcRoots -or $wcRoots.Count -eq 0) {
    return [pscustomobject]@{
      files = @()
      meta = @()
      excluded = @()
    }
  }

  # 你要求的行为：对“每个 svn 工作副本根目录”分别取
  # 1) 最近一次由指定作者提交的 revision
  # 2) 该 revision 在该工作副本中的 .cs 变更文件
  # 最后把所有工作副本结果汇总（并集去重），不要用单个 revision 统治所有目录。
  foreach ($wcRoot in $wcRoots) {
    $entry = Get-SvnFirstLogEntryByAuthorInWorkingCopy -wcRoot $wcRoot -author $SvnAuthor
    if (-not $entry) { continue }

    $rev = $entry.Rev

    if ($SvnTodayOnly -and $entry.CommitDay -and $entry.CommitDay -ne $todayLocal) {
      $wcFiles = @(Get-SvnChangedCsFilesForRevisionInWorkingCopy -wcRoot $wcRoot -rev $rev)
      $svnExcluded += [pscustomobject]@{
        wcRoot = $wcRoot
        rev = $rev
        author = $entry.Author
        commitDay = $entry.CommitDay.ToString('yyyy-MM-dd')
        scriptToday = $todayLocal.ToString('yyyy-MM-dd')
        reason = "SvnTodayOnly：该工作副本下作者的最近提交日期 $($entry.CommitDay.ToString('yyyy-MM-dd')) 不是本地当日 $($todayLocal.ToString('yyyy-MM-dd'))，已排除本批 .cs（共 $($wcFiles.Count) 个）"
        excludedCsFiles = @($wcFiles)
      }
      continue
    }

    $wcFiles = @(Get-SvnChangedCsFilesForRevisionInWorkingCopy -wcRoot $wcRoot -rev $rev)
    $files += $wcFiles
    $svnMeta += [pscustomobject]@{
      wcRoot = $wcRoot
      rev = $rev
      csChangedCount = $wcFiles.Count
      commitDay = if ($entry.CommitDay) { $entry.CommitDay.ToString('yyyy-MM-dd') } else { "" }
      svnTodayOnly = $SvnTodayOnly
    }
  }

  $uniqueFiles = $files | Sort-Object -Unique
  return [pscustomobject]@{
    files = $uniqueFiles
    meta = $svnMeta
    excluded = $svnExcluded
  }
}

$gitFiles = @()
$svnFiles = @()
$svnMeta = @()
$svnExcluded = @()

if (-not $SvnOnly) { $gitFiles = Get-GitLastChangedCsFiles -author $GitAuthor }
if (-not $GitOnly) {
  $svnResult = Get-SvnLastChangedCsFiles
  $svnFiles = $svnResult.files
  $svnMeta = $svnResult.meta
  $svnExcluded = $svnResult.excluded
}

[pscustomobject]@{
  Git = @($gitFiles)
  Svn = @($svnFiles)
  SvnMeta = @($svnMeta)
  SvnExcluded = @($svnExcluded)
  SvnTodayOnly = $SvnTodayOnly
} | ConvertTo-Json -Depth 8

