#requires -Version 5.1
[CmdletBinding(DefaultParameterSetName='Create')]
param(
    [Parameter(Mandatory,ParameterSetName='Create')][switch]$Create,
    [Parameter(Mandatory,ParameterSetName='Trust')][switch]$Trust,
    [Parameter(Mandatory,ParameterSetName='Untrust')][switch]$Untrust,
    [Parameter(Mandatory,ParameterSetName='DeleteKey')][switch]$DeleteKey,
    [Parameter(Mandatory,ParameterSetName='Create')][Parameter(Mandatory,ParameterSetName='Trust')][string]$PublicCertificate,
    [Parameter(Mandatory,ParameterSetName='Trust')][Parameter(Mandatory,ParameterSetName='Untrust')][Parameter(Mandatory,ParameterSetName='DeleteKey')][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Thumbprint
)
$ErrorActionPreference = 'Stop'
$subject = 'CN=AileArc Development Test Only'
function Assert-TestCertificate($Certificate) {
    $eku = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    $constraints = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($Certificate.Subject -cne $subject -or $Certificate.Issuer -cne $subject -or $eku.Count -ne 1 -or $eku[0] -ne '1.3.6.1.5.5.7.3.3' -or $constraints.Count -ne 1 -or $constraints[0].CertificateAuthority) { throw 'Not an AileArc development end-entity code-signing certificate.' }
}
if ($Create -or $Trust) {
    # Resolve relative to this PowerShell runspace's Set-Location, not the process working directory.
    $provider = $null; $drive = $null
    $file = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublicCertificate, [ref]$provider, [ref]$drive)
    if ($provider.Name -ne 'FileSystem') { throw 'Use a filesystem path for the public certificate.' }
    if ([IO.Path]::GetExtension($file) -ine '.cer') { throw 'Use a public .cer file.' }
    if ($Trust -and !(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Public certificate file not found: $file" }
}
if ($Create) {
    if ((Test-Path -LiteralPath $file) -or !(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($file)) -PathType Container)) { throw 'Choose a new .cer file in an existing folder.' }
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'AileArc local menu test' -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(90) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={critical}{text}ca=0')
    try { Assert-TestCertificate $cert; Export-Certificate -Cert $cert -FilePath $file -NoClobber | Out-Null }
    catch { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey; throw }
    $cert.Thumbprint; return
}
$trustPath = "Cert:\LocalMachine\TrustedPeople\$Thumbprint"
if ($DeleteKey) {
    if (Test-Path -LiteralPath $trustPath) { throw 'Remove machine trust first.' }
    $keyPath = "Cert:\CurrentUser\My\$Thumbprint"
    if (Test-Path -LiteralPath $keyPath) { Assert-TestCertificate (Get-Item -LiteralPath $keyPath); Remove-Item -LiteralPath $keyPath -DeleteKey }
    return
}
if ($Trust) {
    if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($file) -ne 'Cert') { throw 'Expected a public certificate only.' }
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($file)
    Assert-TestCertificate $cert
    if ($cert.HasPrivateKey -or $cert.Thumbprint -ine $Thumbprint -or $cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) { throw 'Fingerprint, public key file, or validity mismatch.' }
}
if (!([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) { throw 'Run this explicit machine trust operation in an administrator PowerShell.' }
if ($Trust) {
    if (!(Test-Path -LiteralPath $trustPath)) {
        # Import the exact object checked above rather than reopening an elevated file path.
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
        try { $store.Open('ReadWrite'); $store.Add($cert) } finally { $store.Close() }
    }
    if (!(Test-Path -LiteralPath $trustPath)) { throw 'Trust installation not confirmed.' }
} elseif ($Untrust -and (Test-Path -LiteralPath $trustPath)) {
    Assert-TestCertificate (Get-Item -LiteralPath $trustPath); Remove-Item -LiteralPath $trustPath
}
