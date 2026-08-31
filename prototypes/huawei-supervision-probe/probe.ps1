param(
    [ValidateSet("build", "device", "benchmark", "collect")]
    [string]$Action = "build",
    [switch]$AcceptSdkLicense
)

$ErrorActionPreference = "Stop"
$PrototypeRoot = $PSScriptRoot
$RepositoryRoot = (Resolve-Path (Join-Path $PrototypeRoot "..\..")).Path
$ToolRoot = Join-Path $RepositoryRoot ".tools\android-probe"
$AndroidSdk = Join-Path $ToolRoot "sdk"
$GradleHome = Join-Path $ToolRoot "gradle-9.4.1"
$OutputRoot = Join-Path $PrototypeRoot "out"
$SdkManager = Join-Path $AndroidSdk "cmdline-tools\latest\bin\sdkmanager.bat"
$Adb = Join-Path $AndroidSdk "platform-tools\adb.exe"
$Gradle = Join-Path $GradleHome "bin\gradle.bat"
$PackageName = "com.jarvis.probe"
$GoogleAndroidRepository = "https://googledownloads.cn/android/repository/"

function Assert-PathInsideToolRoot([string]$Path) {
    $fullToolRoot = [System.IO.Path]::GetFullPath($ToolRoot)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullToolRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside tool root: $fullPath"
    }
}

function Download-Verified(
    [string]$Url,
    [string]$Destination,
    [string]$ExpectedSha256
) {
    if (Test-Path -LiteralPath $Destination) {
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash.ToLowerInvariant()
        if ($actual -eq $ExpectedSha256.ToLowerInvariant()) {
            return
        }
    }
    Write-Host "Downloading $Url"
    & curl.exe -L --fail --continue-at - `
        --retry 8 --retry-delay 3 --retry-all-errors `
        --connect-timeout 30 --speed-time 120 --speed-limit 1024 `
        --output $Destination $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed for $Url"
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Destination. Expected $ExpectedSha256, got $actual"
    }
}

function Ensure-Gradle {
    if (Test-Path -LiteralPath $Gradle) {
        return
    }
    New-Item -ItemType Directory -Force $ToolRoot | Out-Null
    $zip = Join-Path $ToolRoot "gradle-9.4.1-bin.zip"
    $shaUrl = "https://services.gradle.org/distributions/gradle-9.4.1-bin.zip.sha256"
    $shaResponse = Invoke-WebRequest -Uri $shaUrl
    $expected = if ($shaResponse.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($shaResponse.Content).Trim()
    } else {
        $shaResponse.Content.ToString().Trim()
    }
    Download-Verified "https://services.gradle.org/distributions/gradle-9.4.1-bin.zip" $zip $expected
    Write-Host "Expanding Gradle 9.4.1"
    Expand-Archive -LiteralPath $zip -DestinationPath $ToolRoot -Force
}

function Confirm-AndroidSdkLicense {
    if ($AcceptSdkLicense) {
        return
    }
    $answer = Read-Host "Android SDK components require the Google Android SDK License Agreement. Type ACCEPT to confirm that you accept it"
    if ($answer -ne "ACCEPT") {
        throw "Android SDK license was not accepted; SDK installation stopped."
    }
}

function Ensure-AndroidSdk {
    if ((Test-Path -LiteralPath $SdkManager) -and (Test-Path -LiteralPath $Adb)) {
        return
    }
    Confirm-AndroidSdkLicense
    New-Item -ItemType Directory -Force $ToolRoot | Out-Null
    $zip = Join-Path $ToolRoot "commandlinetools-win-15859902_latest.zip"
    Download-Verified `
        "${GoogleAndroidRepository}commandlinetools-win-15859902_latest.zip" `
        $zip `
        "90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a"

    $unpack = Join-Path $ToolRoot "cmdline-unpack"
    Assert-PathInsideToolRoot $unpack
    if (Test-Path -LiteralPath $unpack) {
        Remove-Item -LiteralPath $unpack -Recurse
    }
    New-Item -ItemType Directory -Force $unpack | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
    $latest = Join-Path $AndroidSdk "cmdline-tools\latest"
    New-Item -ItemType Directory -Force (Split-Path -Parent $latest) | Out-Null
    if (Test-Path -LiteralPath $latest) {
        Assert-PathInsideToolRoot $latest
        Remove-Item -LiteralPath $latest -Recurse
    }
    Move-Item -LiteralPath (Join-Path $unpack "cmdline-tools") -Destination $latest
    Remove-Item -LiteralPath $unpack -Recurse

    $previousRepository = $env:SDK_TEST_BASE_URL
    $env:SDK_TEST_BASE_URL = $GoogleAndroidRepository
    try {
        $yes = (1..40 | ForEach-Object { "y" }) -join [Environment]::NewLine
        $yes | & $SdkManager --sdk_root=$AndroidSdk --licenses | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Android SDK license acceptance failed" }
        & $SdkManager --sdk_root=$AndroidSdk "platform-tools" "platforms;android-36" "build-tools;36.0.0"
        if ($LASTEXITCODE -ne 0) { throw "Android SDK component installation failed" }
    } finally {
        $env:SDK_TEST_BASE_URL = $previousRepository
    }
}

function Build-Probe {
    Ensure-Gradle
    Ensure-AndroidSdk
    New-Item -ItemType Directory -Force $OutputRoot | Out-Null
    $env:ANDROID_SDK_ROOT = $AndroidSdk
    $previousVersionCode = $env:ORG_GRADLE_PROJECT_probeVersionCode
    $previousVersionName = $env:ORG_GRADLE_PROJECT_probeVersionName
    Push-Location $PrototypeRoot
    try {
        $env:ORG_GRADLE_PROJECT_probeVersionCode = "1"
        $env:ORG_GRADLE_PROJECT_probeVersionName = "0.1-probe"
        & $Gradle --no-daemon clean assembleDebug
        if ($LASTEXITCODE -ne 0) { throw "Probe v1 build failed" }
        Copy-Item -LiteralPath "app\build\outputs\apk\debug\app-debug.apk" -Destination (Join-Path $OutputRoot "jarvis-probe-v1.apk") -Force

        $env:ORG_GRADLE_PROJECT_probeVersionCode = "2"
        $env:ORG_GRADLE_PROJECT_probeVersionName = "0.2-probe"
        & $Gradle --no-daemon clean assembleDebug
        if ($LASTEXITCODE -ne 0) { throw "Probe v2 build failed" }
        Copy-Item -LiteralPath "app\build\outputs\apk\debug\app-debug.apk" -Destination (Join-Path $OutputRoot "jarvis-probe-v2.apk") -Force
    } finally {
        $env:ORG_GRADLE_PROJECT_probeVersionCode = $previousVersionCode
        $env:ORG_GRADLE_PROJECT_probeVersionName = $previousVersionName
        Pop-Location
    }

    Get-FileHash -Algorithm SHA256 (Join-Path $OutputRoot "jarvis-probe-v1.apk"), (Join-Path $OutputRoot "jarvis-probe-v2.apk") |
        Format-Table -AutoSize
}

function Assert-Device {
    & $Adb start-server | Out-Null
    $state = (& $Adb get-state 2>$null).Trim()
    if ($state -ne "device") {
        throw "No authorized phone found. Connect the Mate 70 Pro+ by USB, choose data transfer if prompted, enable USB debugging, and approve this computer."
    }
}

function Get-TargetPackageFacts {
    $targets = @(
        "com.ss.android.ugc.aweme",
        "tv.danmaku.bili",
        "com.xingin.xhs",
        "com.tencent.mm"
    )
    foreach ($target in $targets) {
        $path = (& $Adb shell pm path $target 2>$null | Out-String).Trim()
        if ($path.StartsWith("package:")) {
            $version = (& $Adb shell dumpsys package $target | Select-String -Pattern "versionName=" | Select-Object -First 1).ToString().Trim()
            "$target`tinstalled`t$version"
        } else {
            "$target`tnot-found"
        }
    }
}

function Prepare-Device {
    Build-Probe
    Assert-Device
    Write-Host "Installing v1, then same-signature v2 upgrade"
    & $Adb install -r (Join-Path $OutputRoot "jarvis-probe-v1.apk")
    if ($LASTEXITCODE -ne 0) { throw "Probe v1 installation failed" }
    & $Adb install -r (Join-Path $OutputRoot "jarvis-probe-v2.apk")
    if ($LASTEXITCODE -ne 0) { throw "Probe v2 upgrade failed" }

    $facts = @(
        "manufacturer=$((& $Adb shell getprop ro.product.manufacturer).Trim())"
        "model=$((& $Adb shell getprop ro.product.model).Trim())"
        "androidApi=$((& $Adb shell getprop ro.build.version.sdk).Trim())"
        "androidRelease=$((& $Adb shell getprop ro.build.version.release).Trim())"
        "buildDisplay=$((& $Adb shell getprop ro.build.display.id).Trim())"
        "probe=$((& $Adb shell dumpsys package $PackageName | Select-String -Pattern 'versionName=' | Select-Object -First 1).ToString().Trim())"
        ""
        "target packages:"
        (Get-TargetPackageFacts)
    )
    New-Item -ItemType Directory -Force $OutputRoot | Out-Null
    $facts | Set-Content -LiteralPath (Join-Path $OutputRoot "device-facts.txt") -Encoding utf8
    $facts | Out-Host
    & $Adb shell am start -n "$PackageName/.ProbeActivity" | Out-Host
    Write-Host "The probe is open. Grant each permission in order; do not use adb appops to bypass the real owner flow."
}

function Run-Benchmark {
    Ensure-AndroidSdk
    Assert-Device
    $missing = Get-TargetPackageFacts | Where-Object { $_ -like "*not-found*" }
    if ($missing) {
        $missing | Out-Host
        throw "One or more target package candidates are not installed. Record the actual package before benchmarking."
    }

    & $Adb shell am force-stop $PackageName | Out-Null
    & $Adb shell am start --ez clearLog true --ei startMinutes 30 -n "$PackageName/.ProbeActivity" | Out-Host
    Start-Sleep -Seconds 2
    $policyState = (& $Adb shell run-as $PackageName cat shared_prefs/probe_policy.xml 2>$null | Out-String)
    $probeLog = (& $Adb shell run-as $PackageName cat files/probe-events.jsonl 2>$null | Out-String)
    if ($policyState -notmatch '<boolean name="active" value="true"' -or
        $probeLog -notmatch '"type":"policy_started"') {
        throw "The probe policy did not start; stopping before the benchmark to avoid a false result."
    }
    $targets = @(
        "com.ss.android.ugc.aweme",
        "tv.danmaku.bili",
        "com.xingin.xhs",
        "com.tencent.mm"
    )
    $count = 0
    $launches = [System.Collections.Generic.List[object]]::new()
    foreach ($target in $targets) {
        foreach ($iteration in 1..25) {
            $count++
            Write-Progress -Activity "100 target foreground switches" -Status "$target iteration $iteration" -PercentComplete $count
            & $Adb shell input keyevent KEYCODE_HOME | Out-Null
            Start-Sleep -Milliseconds 600
            $launches.Add([pscustomobject]@{
                package = $target
                iteration = $iteration
                commandEpochMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            })
            & $Adb shell monkey -p $target -c android.intent.category.LAUNCHER 1 | Out-Null
            Start-Sleep -Milliseconds 1200
        }
    }
    & $Adb shell input keyevent KEYCODE_HOME | Out-Null
    Write-Progress -Activity "100 target foreground switches" -Completed
    $launches | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputRoot "benchmark-launches.json") -Encoding utf8
    Collect-Results
}

function Collect-Results {
    Ensure-AndroidSdk
    Assert-Device
    New-Item -ItemType Directory -Force $OutputRoot | Out-Null
    $logPath = Join-Path $OutputRoot "probe-events.jsonl"
    $lines = & $Adb shell run-as $PackageName cat files/probe-events.jsonl
    if ($LASTEXITCODE -ne 0) { throw "Could not read app-private probe log" }
    $lines | Set-Content -LiteralPath $logPath -Encoding utf8

    $events = @($lines | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $policyStartEvent = $events | Where-Object type -eq "policy_started" | Select-Object -Last 1
    $policyStartEpoch = if ($policyStartEvent) { [long]$policyStartEvent.eventEpochMs } else { 0 }
    $targetPackages = @(
        "com.ss.android.ugc.aweme", "tv.danmaku.bili", "com.xingin.xhs", "com.tencent.mm"
    )
    $usageTargets = @($events | Where-Object {
        $_.type -eq "foreground" -and $_.source -eq "usage" -and
        $_.package -in $targetPackages -and [long]$_.eventEpochMs -ge $policyStartEpoch
    })
    $accessibilityTargets = @($events | Where-Object {
        $_.type -eq "foreground" -and $_.source -eq "accessibility" -and
        $_.package -in $targetPackages -and [long]$_.eventEpochMs -ge $policyStartEpoch
    })
    $launchPath = Join-Path $OutputRoot "benchmark-launches.json"
    $expectedLaunches = if (Test-Path -LiteralPath $launchPath) {
        @(Get-Content -Raw -LiteralPath $launchPath | ConvertFrom-Json)
    } else {
        @()
    }
    function Match-Launches([object[]]$CandidateEvents) {
        $usedEventIds = [System.Collections.Generic.HashSet[string]]::new()
        $matchedEvents = [System.Collections.Generic.List[object]]::new()
        $missedLaunches = [System.Collections.Generic.List[object]]::new()
        foreach ($expected in $expectedLaunches) {
            # Match each launch to one unique device event. The one-second lead
            # allowance covers measured host/device clock skew plus adb startup.
            $match = $CandidateEvents | Where-Object {
                $_.package -eq $expected.package -and
                -not $usedEventIds.Contains([string]$_.id) -and
                [long]$_.eventEpochMs -ge ([long]$expected.commandEpochMs - 1000) -and
                [long]$_.eventEpochMs -le ([long]$expected.commandEpochMs + 3000)
            } | Sort-Object @{ Expression = {
                [Math]::Abs([long]$_.eventEpochMs - [long]$expected.commandEpochMs)
            } } | Select-Object -First 1
            if ($match) {
                [void]$usedEventIds.Add([string]$match.id)
                $matchedEvents.Add($match)
            } else {
                $missedLaunches.Add($expected)
            }
        }
        return [pscustomobject]@{
            matched = @($matchedEvents)
            missed = @($missedLaunches)
        }
    }
    $usageMatch = Match-Launches $usageTargets
    $accessibilityMatch = Match-Launches $accessibilityTargets
    $usageLatencies = @($usageMatch.matched | ForEach-Object { [long]$_.latencyMs } | Sort-Object)
    $accessibilityLatencies = @($accessibilityMatch.matched | ForEach-Object { [long]$_.latencyMs } | Sort-Object)
    function Percentile([long[]]$Values, [double]$P) {
        if ($Values.Count -eq 0) { return 0 }
        $index = [Math]::Max(0, [Math]::Min($Values.Count - 1, [Math]::Ceiling($P * $Values.Count) - 1))
        return $Values[$index]
    }
    $summary = @(
        "usageTargetEvents=$($usageTargets.Count)"
        "expectedLaunches=$($expectedLaunches.Count)"
        "matchedLaunches=$($usageMatch.matched.Count)"
        "missedLaunches=$($usageMatch.missed.Count)"
        "unmatchedTargetEvents=$($usageTargets.Count - $usageMatch.matched.Count)"
        "usageP50Ms=$(Percentile $usageLatencies 0.50)"
        "usageP95Ms=$(Percentile $usageLatencies 0.95)"
        "usageMaxMs=$(if ($usageLatencies.Count) { $usageLatencies[-1] } else { 0 })"
        "accessibilityTargetEvents=$($accessibilityTargets.Count)"
        "accessibilityMatchedLaunches=$($accessibilityMatch.matched.Count)"
        "accessibilityMissedLaunches=$($accessibilityMatch.missed.Count)"
        "accessibilityUnmatchedTargetEvents=$($accessibilityTargets.Count - $accessibilityMatch.matched.Count)"
        "accessibilityP50Ms=$(Percentile $accessibilityLatencies 0.50)"
        "accessibilityP95Ms=$(Percentile $accessibilityLatencies 0.95)"
        "accessibilityMaxMs=$(if ($accessibilityLatencies.Count) { $accessibilityLatencies[-1] } else { 0 })"
        "blockTransitions=$(($events | Where-Object type -eq 'blocked').Count)"
        "temporaryAccess=$(($events | Where-Object type -eq 'temporary_access_started').Count)"
        "availabilityFailures=$(($events | Where-Object { $_.type -eq 'availability' -and $_.detail -like 'unavailable*' }).Count)"
    )
    $summary | Set-Content -LiteralPath (Join-Path $OutputRoot "probe-summary.txt") -Encoding utf8
    $summary | Out-Host
    Write-Host "Raw private-device log saved locally to $logPath. Review DEVICE-TEST.md before deciding pass/fail."
}

switch ($Action) {
    "build" { Build-Probe }
    "device" { Prepare-Device }
    "benchmark" { Run-Benchmark }
    "collect" { Collect-Results }
}
