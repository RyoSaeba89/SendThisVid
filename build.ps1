# Builds SendThisVid.exe: one portable exe with ffmpeg embedded. No SDK needed, uses the csc.exe shipped with Windows.
# Optional signing: set SIGN_CERT_THUMBPRINT (cert in your CurrentUser\My store) or SIGN_PFX + SIGN_PFX_PASSWORD.
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $build | Out-Null
$gz = Join-Path $build 'ffmpeg.exe.gz'
$exePath = Join-Path $PSScriptRoot 'SendThisVid.exe'

if (-not (Test-Path $gz)) {
    # Pinned gyan.dev essentials build, from its GitHub mirror (gyan.dev itself is very slow)
    $url = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
    $sha = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
    $zip = Join-Path $build 'ffmpeg.zip'
    Write-Host "Downloading ffmpeg 9.0.2 (gyan.dev essentials build)..."
    curl.exe -fsSL --retry 3 -o $zip $url  # Invoke-WebRequest stalls on large files
    if ($LASTEXITCODE) { throw "ffmpeg download failed" }
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $sha) { throw "ffmpeg checksum mismatch" }
    $unz = Join-Path $build 'ffmpeg-unzipped'
    Expand-Archive $zip $unz -Force
    $exe = Get-ChildItem $unz -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    $in = [IO.File]::OpenRead($exe.FullName)
    $out = [IO.Compression.GZipStream]::new([IO.File]::Create($gz), [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($out); $out.Dispose(); $in.Dispose()
    Remove-Item $zip, $unz -Recurse -Force
}

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exePath" "/win32icon:$PSScriptRoot\assets\icon.ico" `
    "/resource:$gz,ffmpeg.exe.gz" "/resource:$PSScriptRoot\Ui.xaml,Ui.xaml" "/lib:$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    "$PSScriptRoot\SendThisVid.cs" "$PSScriptRoot\Ui.cs"
if ($LASTEXITCODE) { throw "Compilation failed" }

if ($env:SIGN_CERT_THUMBPRINT) { $cert = Get-Item "Cert:\CurrentUser\My\$env:SIGN_CERT_THUMBPRINT" }
elseif ($env:SIGN_PFX) { $cert = Get-PfxCertificate $env:SIGN_PFX -Password (ConvertTo-SecureString $env:SIGN_PFX_PASSWORD -AsPlainText -Force) }
else {
    # Default: the maintainer's self-signed cert (public part in signing/), newest first
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq 'CN=RyoSaeba89' |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}
if ($cert) {
    $sig = Set-AuthenticodeSignature $exePath $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    # A self-signed cert reports UnknownError (untrusted root) unless trusted on this PC; the signature is still applied
    if (-not $sig.SignerCertificate) { throw "Signing failed: $($sig.StatusMessage)" }
    "Signed by $($cert.Subject) ($($cert.Thumbprint)), local status: $($sig.Status)"
} else { Write-Warning "Not signed (no code-signing certificate found)" }

"{0:N1} MB -> SendThisVid.exe" -f ((Get-Item $exePath).Length / 1MB)
