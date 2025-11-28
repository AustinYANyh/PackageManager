param(
  [switch]$Build,
  [string]$Configuration = "Release",
  [string]$PfxPath,
  [string]$PfxPassword,
  [string]$SubjectName,
  [string]$TimestampUrl,
  [string]$MSBuildPath,
  [string]$TargetExePath
)

function Exec($cmd, $args) {
  & $cmd @args
  return $LASTEXITCODE
}

$configPath = Join-Path $PSScriptRoot "sign.config.json"
if (Test-Path -LiteralPath $configPath) {
  $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  if (-not $PSBoundParameters.ContainsKey('Configuration') -and $json.Configuration) { $Configuration = $json.Configuration }
  if (-not $PSBoundParameters.ContainsKey('PfxPath') -and $json.PfxPath) { $PfxPath = $json.PfxPath }
  if (-not $PSBoundParameters.ContainsKey('PfxPassword') -and $json.PfxPassword) { $PfxPassword = $json.PfxPassword }
  if (-not $PSBoundParameters.ContainsKey('SubjectName') -and $json.SubjectName) { $SubjectName = $json.SubjectName }
  if (-not $PSBoundParameters.ContainsKey('TimestampUrl') -and $json.TimestampUrl) { $TimestampUrl = $json.TimestampUrl }
  if (-not $PSBoundParameters.ContainsKey('MSBuildPath') -and $json.MSBuildPath) { $MSBuildPath = $json.MSBuildPath }
  if (-not $PSBoundParameters.ContainsKey('TargetExePath') -and $json.TargetExePath) { $TargetExePath = $json.TargetExePath }
  if (-not $PSBoundParameters.ContainsKey('Build') -and $json.Build) { $Build = [bool]$json.Build }
}

if (-not $TimestampUrl) { $TimestampUrl = "http://timestamp.digicert.com" }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $TargetExePath) { $TargetExePath = Join-Path $repoRoot ("bin\" + $Configuration + "\PackageManager.exe") }
$targetFull = Resolve-Path -LiteralPath $TargetExePath -ErrorAction SilentlyContinue
if (-not $targetFull -and -not $Build) {
  Write-Error "Target exe not found: $TargetExePath"
  exit 1
}

if ($Build) {
  $proj = Join-Path $repoRoot "PackageManager.csproj"
  if (-not (Test-Path -LiteralPath $proj)) { Write-Error "Project not found: $proj"; exit 1 }
  if (-not $MSBuildPath) {
    $msbuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($msbuildCmd) { $MSBuildPath = $msbuildCmd.Path }
  }
  if (-not $MSBuildPath) { Write-Error "MSBuild.exe not found"; exit 1 }
  $code = Exec $MSBuildPath @($proj, "/t:Build", "/p:Configuration=$Configuration")
  if ($code -ne 0) { exit $code }
}

$targetFull = Resolve-Path -LiteralPath $TargetExePath -ErrorAction SilentlyContinue
if (-not $targetFull) { Write-Error "Target exe not found: $TargetExePath"; exit 1 }

$signScript = Join-Path $PSScriptRoot "SignExe.ps1"
if (-not (Test-Path -LiteralPath $signScript)) { Write-Error "SignExe.ps1 not found"; exit 1 }

$args = @("-FilePath", $targetFull.Path)
if ($PfxPath) { $args += @("-CertPath", $PfxPath) }
if ($PfxPassword) { $args += @("-CertPassword", $PfxPassword) }
if ($SubjectName) { $args += @("-SubjectName", $SubjectName) }
if ($TimestampUrl) { $args += @("-TimestampUrl", $TimestampUrl) }

& powershell -ExecutionPolicy Bypass -File $signScript @args
exit $LASTEXITCODE

