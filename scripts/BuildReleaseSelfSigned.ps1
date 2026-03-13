param(
  [string]$Configuration = "Release",
  [string]$ProjectPath,
  [string]$MSBuildPath,
  [string]$SubjectName,
  [int]$ValidYears,
  [string]$Thumbprint,
  [string]$SignToolPath,
  [string]$TimestampUrl,
  [bool]$TrustCurrentUser = $true
)

function Exec {
  param(
    [Parameter(Mandatory=$true)][string]$Cmd,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$CommandArgs
  )

  & $Cmd @CommandArgs
  return $LASTEXITCODE
}

$configPath = Join-Path $PSScriptRoot "selfsign.config.json"
if (Test-Path -LiteralPath $configPath) {
  $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  if (-not $PSBoundParameters.ContainsKey('Configuration') -and $json.Configuration) { $Configuration = $json.Configuration }
  if (-not $PSBoundParameters.ContainsKey('ProjectPath') -and $json.ProjectPath) { $ProjectPath = $json.ProjectPath }
  if (-not $PSBoundParameters.ContainsKey('MSBuildPath') -and $json.MSBuildPath) { $MSBuildPath = $json.MSBuildPath }
  if (-not $PSBoundParameters.ContainsKey('SubjectName') -and $json.SubjectName) { $SubjectName = $json.SubjectName }
  if (-not $PSBoundParameters.ContainsKey('ValidYears') -and $json.ValidYears) { $ValidYears = [int]$json.ValidYears }
  if (-not $PSBoundParameters.ContainsKey('Thumbprint') -and $json.Thumbprint) { $Thumbprint = $json.Thumbprint }
  if (-not $PSBoundParameters.ContainsKey('SignToolPath') -and $json.SignToolPath) { $SignToolPath = $json.SignToolPath }
  if (-not $PSBoundParameters.ContainsKey('TimestampUrl') -and $json.TimestampUrl) { $TimestampUrl = $json.TimestampUrl }
  if (-not $PSBoundParameters.ContainsKey('TrustCurrentUser') -and $null -ne $json.TrustCurrentUser) { $TrustCurrentUser = [bool]$json.TrustCurrentUser }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ProjectPath) { $ProjectPath = Join-Path $repoRoot "PackageManager.csproj" }
if (-not [System.IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath = Join-Path $repoRoot $ProjectPath }
if ($SignToolPath -and -not [System.IO.Path]::IsPathRooted($SignToolPath)) { $SignToolPath = Join-Path $repoRoot $SignToolPath }

if (-not (Test-Path -LiteralPath $ProjectPath)) {
  Write-Error "Project not found: $ProjectPath"
  exit 1
}

if (-not $MSBuildPath) {
  $msbuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
  if ($msbuildCmd) { $MSBuildPath = $msbuildCmd.Path }
}

if (-not $MSBuildPath) {
  Write-Error "MSBuild.exe not found"
  exit 1
}

$buildArgs = @(
  $ProjectPath,
  "/t:Build",
  "/p:Configuration=$Configuration",
  "/p:SelfSign_Enabled=true"
)

if ($SubjectName) { $buildArgs += "/p:SelfSign_SubjectName=$SubjectName" }
if ($ValidYears) { $buildArgs += "/p:SelfSign_ValidYears=$ValidYears" }
if ($Thumbprint) { $buildArgs += "/p:SelfSign_Thumbprint=$Thumbprint" }
if ($SignToolPath) { $buildArgs += "/p:SelfSign_SignToolPath=$SignToolPath" }
if ($TimestampUrl) { $buildArgs += "/p:SelfSign_TimestampUrl=$TimestampUrl" }
$buildArgs += "/p:SelfSign_TrustCurrentUser=$TrustCurrentUser"

$code = Exec $MSBuildPath @buildArgs
exit $code
