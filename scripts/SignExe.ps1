param(
  [Parameter(Mandatory=$true)][string]$FilePath,
  [string]$CertPath,
  [string]$CertPassword,
  [string]$SubjectName,
  [string]$TimestampUrl = "http://timestamp.digicert.com",
  [switch]$VerifyOnly
)

function Exec($cmd, $args) {
  & $cmd @args
  return $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $FilePath)) {
  Write-Error "File not found: $FilePath"
  exit 1
}

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
  Write-Error "signtool.exe not found"
  exit 1
}

if ($VerifyOnly) {
  $code = Exec $signtool.Path @("verify", "/pa", $FilePath)
  exit $code
}

if ([string]::IsNullOrWhiteSpace($CertPath) -and [string]::IsNullOrWhiteSpace($SubjectName)) {
  Write-Error "No certificate configured: set -CertPath/-CertPassword or -SubjectName"
  exit 1
}

if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
  $args = @("sign", "/f", $CertPath)
  if (-not [string]::IsNullOrWhiteSpace($CertPassword)) { $args += @("/p", $CertPassword) }
  $args += @("/tr", $TimestampUrl, "/td", "SHA256", "/fd", "SHA256", $FilePath)
  $code = Exec $signtool.Path $args
  if ($code -ne 0) { exit $code }
} elseif (-not [string]::IsNullOrWhiteSpace($SubjectName)) {
  $args = @("sign", "/n", $SubjectName, "/tr", $TimestampUrl, "/td", "SHA256", "/fd", "SHA256", $FilePath)
  $code = Exec $signtool.Path $args
  if ($code -ne 0) { exit $code }
}

$verify = Exec $signtool.Path @("verify", "/pa", $FilePath)
exit $verify

