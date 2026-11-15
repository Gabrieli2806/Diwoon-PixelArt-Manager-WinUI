<#
.SYNOPSIS
    Writes the certificate that signs the .msix: a .pfx for signing and a .cer that users install to trust it.

.DESCRIPTION
    With -Base64Pfx (the MSIX_CERTIFICATE secret in GitHub Actions) the same certificate is used for every release,
    so users trust it once. Without it, a new self-signed certificate is made in memory (nothing is added to the
    certificate store); fine for testing, but each build then needs its own .cer installed.
    The subject must match the Publisher in Package.appxmanifest.
#>
param(
    [Parameter(Mandatory)] [string] $OutputFolder,
    [Parameter(Mandatory)] [string] $Password,
    [string] $Base64Pfx = "",
    [string] $Subject = "CN=Gabrieli2806"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutputFolder | Out-Null
$pfxPath = Join-Path $OutputFolder "signing.pfx"
$cerPath = Join-Path $OutputFolder "DiwoonPixelArtManager.cer"
$flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable

if ($Base64Pfx) {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($Base64Pfx))
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $Password, $flags)
    if ($certificate.Subject -ne $Subject) { throw "The certificate subject '$($certificate.Subject)' does not match '$Subject'." }
}
else {
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
    $usages = New-Object System.Security.Cryptography.OidCollection
    [void]$usages.Add((New-Object System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.3"))) # code signing
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($usages, $false)))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(3))
    [IO.File]::WriteAllBytes($pfxPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
}

[IO.File]::WriteAllBytes($cerPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Write-Output $pfxPath
