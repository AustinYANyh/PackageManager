param(
  # 目标列表：可以是文件路径（*.cs）或目录路径
  [string[]]$Targets,
  # 仓库根（默认 git 顶层目录）
  [string]$RepoRoot = "",
  # 是否仅处理目录直接子级（默认递归）
  [switch]$NoRecursive
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
  if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
    return (Resolve-Path -LiteralPath $RepoRoot).Path
  }
  $top = (git rev-parse --show-toplevel 2>$null).Trim()
  if (-not $top) { throw "Not a git repository (or git unavailable). Please specify -RepoRoot." }
  return $top
}

function Is-CSharpFile([string]$fullPath) {
  if ([string]::IsNullOrWhiteSpace($fullPath)) { return $false }
  $lower = $fullPath.ToLowerInvariant()
  if (-not $lower.EndsWith(".cs")) { return $false }
  if ($lower.EndsWith(".g.cs")) { return $false }
  if ($lower.EndsWith(".generated.cs")) { return $false }
  if ($lower.EndsWith(".designer.cs")) { return $false }
  if ($lower.EndsWith("assemblyinfo.cs")) { return $false }
  return $true
}

function Resolve-TargetToFullPath([string]$target, [string]$root) {
  $t = $target.Trim().Trim('\"').Trim("'")
  if (-not $t) { return $null }
  if ($t.StartsWith("#")) { return $null }

  if ([System.IO.Path]::IsPathRooted($t)) {
    return $t
  }
  return (Join-Path -Path $root -ChildPath ($t -replace "/", [IO.Path]::DirectorySeparatorChar))
}

function Add-DirCsFiles([string]$dir, [bool]$recursive, [System.Collections.Generic.HashSet[string]]$set) {
  if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return }

  $excludeDirNames = @(
    ".git", ".svn", ".vs", ".idea", ".vscode", ".dotnet",
    "bin", "obj", "packages", "node_modules", "dist", "out"
  )

  $stack = New-Object System.Collections.Generic.Stack[string]
  $stack.Push($dir)

  while ($stack.Count -gt 0) {
    $cur = $stack.Pop()

    try {
      Get-ChildItem -LiteralPath $cur -File -Filter "*.cs" -Force -ErrorAction SilentlyContinue |
        Where-Object { Is-CSharpFile $_.FullName } |
        ForEach-Object { [void]$set.Add($_.FullName) }
    } catch {
      # ignore
    }

    if (-not $recursive) { continue }

    try {
      Get-ChildItem -LiteralPath $cur -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $excludeDirNames -notcontains $_.Name } |
        ForEach-Object { $stack.Push($_.FullName) }
    } catch {
      # ignore
    }
  }
}

$root = Get-RepoRoot
$recursive = -not $NoRecursive

$set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

if ($Targets) {
  foreach ($t in $Targets) {
    $full = Resolve-TargetToFullPath -target $t -root $root
    if (-not $full) { continue }
    if (-not (Test-Path -LiteralPath $full)) { continue }

    if (Test-Path -LiteralPath $full -PathType Container) {
      Add-DirCsFiles -dir $full -recursive $recursive -set $set
    } else {
      if (Is-CSharpFile $full) {
        [void]$set.Add((Resolve-Path -LiteralPath $full).Path)
      }
    }
  }
}

$files = @($set) | Sort-Object -Unique
[pscustomobject]@{ Files = @($files) } | ConvertTo-Json -Depth 5

