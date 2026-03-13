param(
  [switch]$Build,
  [string]$Configuration = "Release",
  [string]$PfxPath,
  [string]$PfxPassword,
  [string]$SubjectName,
  [string]$Thumbprint,
  [ValidateSet("CurrentUser", "LocalMachine")][string]$StoreLocation = "CurrentUser",
  [string]$StoreName = "My",
  [string]$SignToolPath,
  [string]$TimestampUrl,
  [string]$MSBuildPath,
  [string]$TargetExePath
)

function Exec {
  param(
    [Parameter(Mandatory=$true)][string]$Cmd,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$CommandArgs
  )

  & $Cmd @CommandArgs
  return $LASTEXITCODE
}

$configPath = Join-Path $PSScriptRoot "sign.config.json"
if (Test-Path -LiteralPath $configPath) {
  $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  if (-not $PSBoundParameters.ContainsKey('Configuration') -and $json.Configuration) { $Configuration = $json.Configuration }
  if (-not $PSBoundParameters.ContainsKey('PfxPath') -and $json.PfxPath) { $PfxPath = $json.PfxPath }
  if (-not $PSBoundParameters.ContainsKey('PfxPassword') -and $json.PfxPassword) { $PfxPassword = $json.PfxPassword }
  if (-not $PSBoundParameters.ContainsKey('SubjectName') -and $json.SubjectName) { $SubjectName = $json.SubjectName }
  if (-not $PSBoundParameters.ContainsKey('Thumbprint') -and $json.Thumbprint) { $Thumbprint = $json.Thumbprint }
  if (-not $PSBoundParameters.ContainsKey('StoreLocation') -and $json.StoreLocation) { $StoreLocation = $json.StoreLocation }
  if (-not $PSBoundParameters.ContainsKey('StoreName') -and $json.StoreName) { $StoreName = $json.StoreName }
  if (-not $PSBoundParameters.ContainsKey('SignToolPath') -and $json.SignToolPath) { $SignToolPath = $json.SignToolPath }
  if (-not $PSBoundParameters.ContainsKey('TimestampUrl') -and $json.TimestampUrl) { $TimestampUrl = $json.TimestampUrl }
  if (-not $PSBoundParameters.ContainsKey('MSBuildPath') -and $json.MSBuildPath) { $MSBuildPath = $json.MSBuildPath }
  if (-not $PSBoundParameters.ContainsKey('TargetExePath') -and $json.TargetExePath) { $TargetExePath = $json.TargetExePath }
  if (-not $PSBoundParameters.ContainsKey('Build') -and $json.Build) { $Build = [bool]$json.Build }
}

if (-not $TimestampUrl) { $TimestampUrl = "http://timestamp.digicert.com" }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $TargetExePath) { $TargetExePath = Join-Path $repoRoot ("bin\" + $Configuration + "\PackageManager.exe") }
if (-not [System.IO.Path]::IsPathRooted($TargetExePath)) { $TargetExePath = Join-Path $repoRoot $TargetExePath }
if ($PfxPath -and -not [System.IO.Path]::IsPathRooted($PfxPath)) { $PfxPath = Join-Path $repoRoot $PfxPath }
if ($SignToolPath -and -not [System.IO.Path]::IsPathRooted($SignToolPath)) { $SignToolPath = Join-Path $repoRoot $SignToolPath }
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
  $code = Exec $MSBuildPath $proj "/t:Build" "/p:Configuration=$Configuration"
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
if ($Thumbprint) { $args += @("-Thumbprint", $Thumbprint) }
if ($StoreLocation) { $args += @("-StoreLocation", $StoreLocation) }
if ($StoreName) { $args += @("-StoreName", $StoreName) }
if ($SignToolPath) { $args += @("-SignToolPath", $SignToolPath) }
if ($TimestampUrl) { $args += @("-TimestampUrl", $TimestampUrl) }

& powershell -ExecutionPolicy Bypass -File $signScript @args
exit $LASTEXITCODE
