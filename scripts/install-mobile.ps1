param(
    [string]$ApkPath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $ApkPath = Join-Path $repositoryRoot "artifacts\v2-preinstall\jarvis-mobile-release-signed.apk"
}
$ApkPath = (Resolve-Path -LiteralPath $ApkPath).Path
$sdk = Join-Path $repositoryRoot ".tools\android-probe\sdk"
$adb = Join-Path $sdk "platform-tools\adb.exe"
$apksigner = Join-Path $sdk "build-tools\36.0.0\apksigner.bat"
if (-not (Test-Path -LiteralPath $adb) -or -not (Test-Path -LiteralPath $apksigner)) {
    throw "Android platform/build tools are missing under .tools\android-probe."
}

& $apksigner verify --verbose --print-certs $ApkPath
if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed; installation stopped." }
& $adb start-server | Out-Null
$state = (& $adb get-state 2>$null | Out-String).Trim()
if ($state -ne "device") {
    throw "No authorized phone is connected. Connect the Mate 70 Pro+ by USB and approve this computer."
}
$manufacturer = (& $adb shell getprop ro.product.manufacturer | Out-String).Trim()
$model = (& $adb shell getprop ro.product.model | Out-String).Trim()
Write-Host "Connected device: $manufacturer $model"
if ($manufacturer -notmatch "HUAWEI" -or $model -notmatch "PLA-AL10|Mate 70") {
    throw "The connected phone is not the confirmed HUAWEI Mate 70 Pro+ target; installation stopped."
}

& $adb install -r $ApkPath
if ($LASTEXITCODE -ne 0) { throw "APK installation failed." }
& $adb shell am start -n "com.jarvis.mobile/.MainActivity" | Out-Host
Write-Host "Jarvis Mobile is installed and open."
Write-Host "Grant Usage Access, overlay, notifications, exact alarm, and Huawei background settings in the visible UI."
Write-Warning "This script deliberately does not grant or bypass any app permission through adb."
