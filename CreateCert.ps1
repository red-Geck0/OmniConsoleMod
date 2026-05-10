# ============================================================
# OmniConsole - Self-Signed Certificate Creator
# Run this script as Administrator in Windows PowerShell
# ============================================================

$subject    = "CN=8bit2qubit"
$outputPath = "C:\OmniConsoleCert.pfx"

Write-Host ""
Write-Host "Creating self-signed certificate..." -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $subject `
    -KeyUsage DigitalSignature `
    -FriendlyName "OmniConsole Signing Cert" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

Write-Host "Certificate created. Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

Write-Host ""
Write-Host "Exporting to $outputPath (no password)..." -ForegroundColor Cyan

$password = [System.Security.SecureString]::new()

Export-PfxCertificate `
    -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" `
    -FilePath $outputPath `
    -Password $password

Write-Host ""
Write-Host "Done! Certificate saved to: $outputPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. In Visual Studio -> Publish -> Create App Packages"
Write-Host "  2. Choose 'Select from file' and pick: $outputPath"
Write-Host "  3. Leave password blank"
Write-Host ""
