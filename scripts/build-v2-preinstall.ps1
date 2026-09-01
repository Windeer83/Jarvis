param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion = "0.2.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
$androidSdk = Join-Path $repositoryRoot ".tools\android-probe\sdk"
$gradle = Join-Path $repositoryRoot ".tools\android-probe\gradle-9.4.1\bin\gradle.bat"
$jdkCandidates = @(
    "D:\Application\JDK21",
    (Join-Path ${env:ProgramFiles} "Android\Android Studio\jbr"),
    $env:JAVA_HOME
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath (Join-Path $_ "bin\java.exe")) }
$jdk = $jdkCandidates | Select-Object -First 1
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Bundled .NET SDK is missing: $dotnet" }
if (-not (Test-Path -LiteralPath $gradle) -or -not (Test-Path -LiteralPath $androidSdk)) {
    throw "Android build tools are missing under .tools\android-probe."
}
if (-not $jdk) { throw "JDK 21 is required for the Android unit-test runner." }

$artifactRoot = Join-Path $repositoryRoot "artifacts\v2-preinstall"
if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Path $artifactRoot | Out-Null

Push-Location $repositoryRoot
try {
    & $dotnet build Jarvis.slnx --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET Release build failed." }
    & $dotnet test Jarvis.slnx --configuration Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET test suite failed." }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $PSScriptRoot "build-t27-installer.ps1") `
        -DotnetPath $dotnet -ProductVersion $ProductVersion `
        -InstallerOutputDirectory (Join-Path $repositoryRoot "artifacts\installer")
    if ($LASTEXITCODE -ne 0) { throw "Windows MSI build failed." }
} finally {
    Pop-Location
}

# Gradle's Windows test runner loses a classpath entry when the checkout path contains
# non-ASCII characters. A temporary drive alias gives it an ASCII project path without
# copying or changing the source tree.
$drive = @("R:", "S:", "T:", "U:", "V:") |
    Where-Object { -not (Test-Path "$_\") } | Select-Object -First 1
if (-not $drive) { throw "No temporary drive letter is available for the Android build." }
$previousJavaHome = $env:JAVA_HOME
$previousAndroidHome = $env:ANDROID_HOME
$previousAndroidSdkRoot = $env:ANDROID_SDK_ROOT
try {
    & subst.exe $drive $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw "Unable to create temporary Android build drive." }
    $env:JAVA_HOME = $jdk
    $env:ANDROID_HOME = "$drive\.tools\android-probe\sdk"
    $env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
    Push-Location "$drive\mobile"
    try {
        & "$drive\.tools\android-probe\gradle-9.4.1\bin\gradle.bat" `
            --no-daemon clean testDebugUnitTest lintDebug assembleDebug assembleRelease `
            "-PjarvisVersionName=$ProductVersion" "-PjarvisVersionCode=2"
        if ($LASTEXITCODE -ne 0) { throw "Android build, tests, or lint failed." }
    } finally {
        Pop-Location
    }
} finally {
    $env:JAVA_HOME = $previousJavaHome
    $env:ANDROID_HOME = $previousAndroidHome
    $env:ANDROID_SDK_ROOT = $previousAndroidSdkRoot
    & subst.exe $drive /d 2>$null
}

$debugApk = Join-Path $repositoryRoot "mobile\app\build\outputs\apk\debug\app-debug.apk"
$unsignedApk = Join-Path $repositoryRoot "mobile\app\build\outputs\apk\release\app-release-unsigned.apk"
$msi = Join-Path $repositoryRoot "artifacts\installer\Jarvis-$ProductVersion-win-x64.msi"
Copy-Item -LiteralPath $debugApk -Destination (Join-Path $artifactRoot "jarvis-mobile-debug.apk")
Copy-Item -LiteralPath $unsignedApk -Destination (Join-Path $artifactRoot "jarvis-mobile-release-unsigned.apk")
Copy-Item -LiteralPath $msi -Destination (Join-Path $artifactRoot "Jarvis-$ProductVersion-win-x64.msi")
$files = Get-ChildItem -LiteralPath $artifactRoot -File
$hashes = $files | Get-FileHash -Algorithm SHA256
$hashes | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content -LiteralPath (Join-Path $artifactRoot "SHA256SUMS.txt") -Encoding ascii
$hashes | Format-Table -AutoSize
Write-Host "Pre-install artifacts are ready in $artifactRoot"
