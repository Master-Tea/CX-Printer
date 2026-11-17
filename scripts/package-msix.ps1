param(
    [string]$Version='1.0.0.1',
    [string]$PfxPath,
    [string]$PfxPassword
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$release=Join-Path $repo 'artifacts\CX-Printer-win-x64'
$stage=Join-Path $repo ('.tools\m'+[guid]::NewGuid().ToString('N').Substring(0,6))
$out=Join-Path $repo 'artifacts\MSIX'
$tools='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
if (!(Test-Path -LiteralPath (Join-Path $release 'CX Printer.exe'))) { throw 'Run scripts/build.ps1 first.' }
if (Test-Path -LiteralPath $stage) { throw 'Staging folder already exists. Use a fresh staging folder before rebuilding.' }
New-Item -ItemType Directory -Path $stage,$out -Force | Out-Null
Get-ChildItem -LiteralPath $release | Where-Object { $_.Name -notin @('DebugUI','DebugData') } | Copy-Item -Destination $stage -Recurse
Get-ChildItem -LiteralPath (Join-Path $repo 'packaging\msix') | Copy-Item -Destination $stage -Recurse
$manifestPath=Join-Path $stage 'AppxManifest.xml'
[xml]$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest.Package.Identity.Version=$Version
$manifest.Save($manifestPath)
$package=Join-Path $out "CX-Printer_${Version}_x64.msix"
& "$tools\makeappx.exe" pack /d $stage /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation/packing failed.' }
if (!$PfxPath) {
    $PfxPath=Join-Path $repo '.tools\MasterTea-MSIX.pfx'
    $passwordPath=Join-Path $repo '.tools\MasterTea-MSIX.password'
    if (Test-Path -LiteralPath $PfxPath) {
        $PfxPassword=Get-Content -LiteralPath $passwordPath -Raw
    } else {
        $passwordBytes=New-Object byte[] 32
        $random=[Security.Cryptography.RandomNumberGenerator]::Create()
        $random.GetBytes($passwordBytes); $random.Dispose()
        $PfxPassword=[Convert]::ToBase64String($passwordBytes)
        $rsa=[Security.Cryptography.RSA]::Create(3072)
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=MasterTea',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
        $oids=[Security.Cryptography.OidCollection]::new()
        $null=$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
        $certificate=$request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5),[DateTimeOffset]::Now.AddYears(3))
        [IO.File]::WriteAllBytes($PfxPath,$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$PfxPassword))
        [IO.File]::WriteAllText($passwordPath,$PfxPassword)
        $certificate.Dispose(); $rsa.Dispose()
    }
}
& "$tools\signtool.exe" sign /fd SHA256 /f $PfxPath /p $PfxPassword $package
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
$signingCertificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath,$PfxPassword)
if ($signingCertificate.Subject -ne 'CN=MasterTea') { throw 'Certificate subject must be CN=MasterTea.' }
[IO.File]::WriteAllBytes((Join-Path $out 'MasterTea.cer'),$signingCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
$signingCertificate.Dispose()
Get-FileHash -LiteralPath $package -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content -LiteralPath "$package.sha256" -Encoding ascii
Write-Output "Signed package: $package"
Write-Output 'Public certificate: artifacts/MSIX/MasterTea.cer'
Write-Output 'Private signing key stays in .tools; back it up securely for future updates.'
