param(
    [Parameter(Mandatory = $true)]
    [string]$KeystorePath,
    [string]$Alias = "jarvis-mobile",
    [string]$UnsignedApk = "",
    [string]$SignedApk = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($UnsignedApk)) {
    $UnsignedApk = Join-Path $repositoryRoot "artifacts\v2-preinstall\jarvis-mobile-release-unsigned.apk"
}
if ([string]::IsNullOrWhiteSpace($SignedApk)) {
    $SignedApk = Join-Path $repositoryRoot "artifacts\v2-preinstall\jarvis-mobile-release-signed.apk"
}
$UnsignedApk = (Resolve-Path -LiteralPath $UnsignedApk).Path
$KeystorePath = [IO.Path]::GetFullPath($KeystorePath)
$SignedApk = [IO.Path]::GetFullPath($SignedApk)
$keytool = "D:\Application\JDK21\bin\keytool.exe"
if (-not (Test-Path -LiteralPath $keytool)) { $keytool = (Get-Command keytool.exe -ErrorAction Stop).Source }
$apksigner = Join-Path $repositoryRoot ".tools\android-probe\sdk\build-tools\36.0.0\apksigner.bat"
if (-not (Test-Path -LiteralPath $apksigner)) { throw "Android apksigner 36.0.0 is missing." }

$secret = Read-Host "Enter a NEW long-term release signing password" -AsSecureString
$confirmation = Read-Host "Enter the same password again to confirm" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
$confirmationPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmation)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $confirmationPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmationPointer)
    if (-not [string]::Equals($plain, $confirmationPlain, [StringComparison]::Ordinal)) {
        throw "The two passwords do not match. No signing file was created or changed."
    }
    $env:JARVIS_MOBILE_SIGNING_PASSWORD = $plain
    if (-not (Test-Path -LiteralPath $KeystorePath)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $KeystorePath) | Out-Null
        & $keytool -genkeypair -v -keystore $KeystorePath -alias $Alias `
            -keyalg RSA -keysize 3072 -validity 10000 `
            -dname "CN=Jarvis Private Mobile, O=Jarvis" `
            -storepass:env JARVIS_MOBILE_SIGNING_PASSWORD `
            -keypass:env JARVIS_MOBILE_SIGNING_PASSWORD
        if ($LASTEXITCODE -ne 0) { throw "Release keystore creation failed." }
    }
    & $apksigner sign --ks $KeystorePath --ks-key-alias $Alias `
        --ks-pass env:JARVIS_MOBILE_SIGNING_PASSWORD `
        --key-pass env:JARVIS_MOBILE_SIGNING_PASSWORD `
        --out $SignedApk $UnsignedApk
    if ($LASTEXITCODE -ne 0) { throw "APK signing failed." }
    & $apksigner verify --verbose --print-certs $SignedApk
    if ($LASTEXITCODE -ne 0) { throw "Signed APK verification failed." }
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    $signedStream = [IO.File]::OpenRead($SignedApk)
    try {
        $signedHash = ([BitConverter]::ToString($hashAlgorithm.ComputeHash($signedStream))).Replace("-", "")
    } finally {
        $signedStream.Dispose()
        $hashAlgorithm.Dispose()
    }
    Write-Host "Signed APK SHA-256: $signedHash"
} finally {
    $env:JARVIS_MOBILE_SIGNING_PASSWORD = $null
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    if ($confirmationPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmationPointer)
    }
    $plain = $null
    $confirmationPlain = $null
}

Write-Host "Signed APK: $SignedApk"
Write-Warning "Back up the keystore and password offline. Losing either makes in-place app upgrades impossible."
