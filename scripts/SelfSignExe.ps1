param(
  [Parameter(Mandatory=$true)][string]$FilePath,
  [string]$SubjectName = "CN=PackageManager Self-Signed",
  [int]$ValidYears = 3,
  [string]$Thumbprint,
  [ValidateSet("CurrentUser", "LocalMachine")][string]$StoreLocation = "CurrentUser",
  [string]$StoreName = "My",
  [string]$SignToolPath,
  [string]$TimestampUrl = "",
  [string]$TrustCurrentUser = "true"
)

function Normalize-Thumbprint([string]$Value) {
  if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
  return ($Value -replace '\s', '').ToUpperInvariant()
}

function ConvertTo-Bool([string]$Value, [bool]$DefaultValue) {
  if ([string]::IsNullOrWhiteSpace($Value)) { return $DefaultValue }

  switch ($Value.Trim().ToLowerInvariant()) {
    "true" { return $true }
    "false" { return $false }
    "1" { return $true }
    "0" { return $false }
    default {
      Write-Error "Invalid boolean value: $Value"
      exit 1
    }
  }
}

function Open-Store([string]$Location, [string]$Name, [System.Security.Cryptography.X509Certificates.OpenFlags]$Flags) {
  $storeLocationEnum = [System.Security.Cryptography.X509Certificates.StoreLocation]::$Location
  $storeNameEnum = [System.Security.Cryptography.X509Certificates.StoreName]::$Name
  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeNameEnum, $storeLocationEnum)
  $store.Open($Flags)
  return $store
}

function Get-CertificateByThumbprint([string]$Location, [string]$Name, [string]$NormalizedThumbprint) {
  if ([string]::IsNullOrWhiteSpace($NormalizedThumbprint)) { return $null }

  $store = Open-Store $Location $Name ([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
  try {
    foreach ($cert in $store.Certificates) {
      $thumbprint = ""
      if ($cert.Thumbprint) { $thumbprint = $cert.Thumbprint.ToUpperInvariant() }
      if ($thumbprint -eq $NormalizedThumbprint) {
        return $cert
      }
    }
  }
  finally {
    $store.Close()
  }

  return $null
}

function Find-ExistingCodeSigningCertificate([string]$Location, [string]$Name, [string]$WantedSubjectName) {
  $store = Open-Store $Location $Name ([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
  try {
    $now = Get-Date
    $codeSigningOid = "1.3.6.1.5.5.7.3.3"

    foreach ($cert in $store.Certificates | Sort-Object NotAfter -Descending) {
      if ($cert.Subject -ne $WantedSubjectName) { continue }
      if ($cert.NotAfter -le $now) { continue }
      if (-not $cert.HasPrivateKey) { continue }

      foreach ($ekuExtension in $cert.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }) {
        foreach ($oid in $ekuExtension.EnhancedKeyUsages) {
          if ($oid.Value -eq $codeSigningOid) {
            return $cert
          }
        }
      }
    }
  }
  finally {
    $store.Close()
  }

  return $null
}

function Ensure-SelfSignedCertificate(
  [string]$WantedThumbprint,
  [string]$WantedSubjectName,
  [string]$Location,
  [string]$Name,
  [int]$Years
) {
  $normalizedThumbprint = Normalize-Thumbprint $WantedThumbprint
  if ($normalizedThumbprint) {
    $existing = Get-CertificateByThumbprint $Location $Name $normalizedThumbprint
    if (-not $existing) {
      Write-Error "Self-signed certificate not found by thumbprint: $normalizedThumbprint"
      exit 1
    }

    return $existing
  }

  $existingBySubject = Find-ExistingCodeSigningCertificate $Location $Name $WantedSubjectName
  if ($existingBySubject) {
    return $existingBySubject
  }

  $now = Get-Date
  $notAfter = $now.AddYears($Years)
  $rsa = [System.Security.Cryptography.RSA]::Create(2048)

  try {
    $dn = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName($WantedSubjectName)
    $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
      $dn,
      $rsa,
      [System.Security.Cryptography.HashAlgorithmName]::SHA256,
      [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
    )

    $request.CertificateExtensions.Add(
      (New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $false))
    )
    $request.CertificateExtensions.Add(
      (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
        $false
      ))
    )

    $ekuCollection = New-Object System.Security.Cryptography.OidCollection
    [void]$ekuCollection.Add((New-Object System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.3", "Code Signing")))
    $request.CertificateExtensions.Add(
      (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($ekuCollection, $false))
    )
    $request.CertificateExtensions.Add(
      (New-Object System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension($request.PublicKey, $false))
    )

    $temporaryCertificate = $request.CreateSelfSigned($now.AddDays(-1), $notAfter)
    $password = [Guid]::NewGuid().ToString("N")
    $pfxBytes = $temporaryCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)

    $keyFlags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor `
      [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    if ($Location -eq "CurrentUser") {
      $keyFlags = $keyFlags -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
    } else {
      $keyFlags = $keyFlags -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet
    }

    $created = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxBytes, $password, $keyFlags)

    $store = Open-Store $Location $Name ([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
      $store.Add($created)
    }
    finally {
      $store.Close()
    }

    return $created
  }
  catch {
    Write-Error "Failed to create self-signed code signing certificate. $($_.Exception.Message)"
    exit 1
  }
  finally {
    if ($temporaryCertificate) { $temporaryCertificate.Dispose() }
    if ($rsa) { $rsa.Dispose() }
  }
}

function Ensure-TrustedForCurrentUser([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
  $shouldTrustCurrentUser = ConvertTo-Bool $TrustCurrentUser $true
  if (-not $shouldTrustCurrentUser) { return }
  if ($StoreLocation -ne "CurrentUser") { return }

  $publicOnlyBytes = $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
  $publicOnly = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$publicOnlyBytes)

  foreach ($trustStoreName in @("Root", "TrustedPublisher")) {
    $store = Open-Store "CurrentUser" $trustStoreName ([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
      $alreadyTrusted = $false
      foreach ($existing in $store.Certificates) {
        $existingThumbprint = ""
        if ($existing.Thumbprint) { $existingThumbprint = $existing.Thumbprint.ToUpperInvariant() }
        if ($existingThumbprint -eq $Certificate.Thumbprint.ToUpperInvariant()) {
          $alreadyTrusted = $true
          break
        }
      }

      if (-not $alreadyTrusted) {
        $store.Add($publicOnly)
      }
    }
    finally {
      $store.Close()
    }
  }

  $publicOnly.Dispose()
}

if (-not (Test-Path -LiteralPath $FilePath)) {
  Write-Error "File not found: $FilePath"
  exit 1
}

$resolvedFilePath = (Resolve-Path -LiteralPath $FilePath).Path
$certificate = Ensure-SelfSignedCertificate $Thumbprint $SubjectName $StoreLocation $StoreName $ValidYears
Ensure-TrustedForCurrentUser $certificate

$signScript = Join-Path $PSScriptRoot "SignExe.ps1"
if (-not (Test-Path -LiteralPath $signScript)) {
  Write-Error "SignExe.ps1 not found"
  exit 1
}

$signArgs = @(
  "-FilePath", $resolvedFilePath,
  "-Thumbprint", $certificate.Thumbprint,
  "-StoreLocation", $StoreLocation,
  "-StoreName", $StoreName
)

if ($SignToolPath) { $signArgs += @("-SignToolPath", $SignToolPath) }
if ($TimestampUrl) { $signArgs += @("-TimestampUrl", $TimestampUrl) }

Write-Host "Using self-signed certificate: $($certificate.Subject) [$($certificate.Thumbprint)]"
& powershell -ExecutionPolicy Bypass -File $signScript @signArgs
exit $LASTEXITCODE
