param(
  [Parameter(Mandatory=$true)][string]$FilePath,
  [string]$CertPath,
  [string]$CertPassword,
  [string]$SubjectName,
  [string]$Thumbprint,
  [ValidateSet("CurrentUser", "LocalMachine")][string]$StoreLocation = "CurrentUser",
  [string]$StoreName = "My",
  [string]$SignToolPath,
  [string]$TimestampUrl = "http://timestamp.digicert.com",
  [switch]$VerifyOnly
)

function Exec {
  param(
    [Parameter(Mandatory=$true)][string]$Cmd,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$CommandArgs
  )

  & $Cmd @CommandArgs
  return $LASTEXITCODE
}

function Resolve-SignToolPath([string]$ExplicitPath) {
  if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
    if (-not (Test-Path -LiteralPath $ExplicitPath)) {
      Write-Error "Configured signtool.exe not found: $ExplicitPath"
      exit 1
    }

    return (Resolve-Path -LiteralPath $ExplicitPath).Path
  }

  $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($signtool) {
    return $signtool.Path
  }

  $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
  if (Test-Path -LiteralPath $kitsRoot) {
    $allCandidates = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
      Sort-Object FullName -Descending
    $candidate = $allCandidates | Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Select-Object -First 1
    if (-not $candidate) {
      $candidate = $allCandidates | Select-Object -First 1
    }

    if ($candidate) {
      return $candidate.FullName
    }
  }

  Write-Error "signtool.exe not found. Install Windows SDK or pass -SignToolPath."
  exit 1
}

if (-not (Test-Path -LiteralPath $FilePath)) {
  Write-Error "File not found: $FilePath"
  exit 1
}

$signtoolPath = Resolve-SignToolPath $SignToolPath
Write-Host "Using SignTool: $signtoolPath"

if ($VerifyOnly) {
  $code = Exec $signtoolPath "verify" "/pa" $FilePath
  exit $code
}

if (
  [string]::IsNullOrWhiteSpace($CertPath) -and
  [string]::IsNullOrWhiteSpace($SubjectName) -and
  [string]::IsNullOrWhiteSpace($Thumbprint)
) {
  Write-Error "No certificate configured: set -CertPath/-CertPassword, or -Thumbprint, or -SubjectName"
  exit 1
}

if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
  $signArgs = @("sign", "/f", $CertPath)
  if (-not [string]::IsNullOrWhiteSpace($CertPassword)) { $signArgs += @("/p", $CertPassword) }
  $signArgs += @("/tr", $TimestampUrl, "/td", "SHA256", "/fd", "SHA256", $FilePath)
  $code = Exec $signtoolPath @signArgs
  if ($code -ne 0) { exit $code }
} else {
  $signArgs = @("sign", "/s", $StoreName)
  if ($StoreLocation -eq "LocalMachine") { $signArgs += "/sm" }
  if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    $normalizedThumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    $signArgs += @("/sha1", $normalizedThumbprint)
  }
  if (-not [string]::IsNullOrWhiteSpace($SubjectName)) {
    $signArgs += @("/n", $SubjectName)
  }
  $signArgs += @("/tr", $TimestampUrl, "/td", "SHA256", "/fd", "SHA256", $FilePath)
  $code = Exec $signtoolPath @signArgs
  if ($code -ne 0) { exit $code }
}

$verify = Exec $signtoolPath "verify" "/pa" $FilePath
exit $verify
