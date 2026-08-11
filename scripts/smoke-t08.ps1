param(
    [string]$DotnetPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $mainCheckoutCandidate = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
    $workspaceRoot = Split-Path -Parent (Split-Path -Parent $repositoryRoot)
    $worktreeCandidate = Join-Path $workspaceRoot "Jarvis\.tools\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $mainCheckoutCandidate) {
        $DotnetPath = $mainCheckoutCandidate
    }
    elseif (Test-Path -LiteralPath $worktreeCandidate) {
        $DotnetPath = $worktreeCandidate
    }
    else {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnetCommand) {
            throw "A .NET 10 SDK could not be found."
        }

        $DotnetPath = $dotnetCommand.Source
    }
}

$DotnetPath = (Resolve-Path $DotnetPath).Path
$coreDll = (Resolve-Path (Join-Path $repositoryRoot "src\Jarvis.Core\bin\Release\net10.0-windows\Jarvis.Core.dll")).Path
$desktopDll = (Resolve-Path (Join-Path $repositoryRoot "src\Jarvis.Desktop\bin\Release\net10.0-windows\Jarvis.Desktop.dll")).Path
$desktopExe = (Resolve-Path (Join-Path $repositoryRoot "src\Jarvis.Desktop\bin\Release\net10.0-windows\Jarvis.Desktop.exe")).Path
$dotnetRoot = Split-Path -Parent $DotnetPath
$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
$smokeDirectory = Join-Path $tempRoot ("Jarvis-T08-Smoke-" + [Guid]::NewGuid().ToString("N"))
$smokeDirectory = [System.IO.Path]::GetFullPath($smokeDirectory)
if (-not $smokeDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Smoke directory escaped the Windows temp directory."
}

function Send-CoreRequest {
    param(
        [string]$PipeName,
        [string]$RequestJson
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $writer = $null
    $reader = $null
    try {
        $pipe.Connect(3000)
        $encoding = [System.Text.UTF8Encoding]::new($false)
        $writer = [System.IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe, $encoding, $true, 1024, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine($RequestJson)
        return ($reader.ReadLine() | ConvertFrom-Json)
    }
    finally {
        if ($writer) { $writer.Dispose() }
        if ($reader) { $reader.Dispose() }
        $pipe.Dispose()
    }
}

function Start-SmokeCore {
    $arguments = 'exec "{0}" --data-dir "{1}" --desktop-path "{2}"' -f $coreDll, $smokeDirectory, $desktopExe
    return Start-Process -FilePath $DotnetPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
}

function Get-CoreDesktopProcesses {
    param([int]$CoreProcessId)

    return @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $CoreProcessId AND Name = 'Jarvis.Desktop.exe'" |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
}

New-Item -ItemType Directory -Path $smokeDirectory | Out-Null
$env:DOTNET_ROOT = $dotnetRoot
$safeUser = -join ([Environment]::UserName.ToCharArray() | ForEach-Object {
    if ([char]::IsLetterOrDigit($_)) { $_ } else { "_" }
})
$sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$pipeName = "Jarvis.Core.$safeUser.$sessionId"
$coreProcess = $null
$restartedCore = $null
$secondDesktop = $null
$ownedDesktopProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$result = $null

try {
    $coreProcess = Start-SmokeCore
    Start-Sleep -Seconds 4
    $initialDesktops = Get-CoreDesktopProcesses -CoreProcessId $coreProcess.Id
    if ($coreProcess.HasExited) {
        throw "Initial Core exited early with code $($coreProcess.ExitCode)."
    }

    if ($initialDesktops.Count -ne 1) {
        throw "Expected one initial Desktop process, found $($initialDesktops.Count)."
    }

    $ownedDesktopProcesses.Add($initialDesktops[0])

    $startAt = [DateTimeOffset]::Now.AddHours(1).ToString("O")
    $prepareJson = [ordered]@{
        operation = "prepare"
        draft = [ordered]@{
            kind = "Computer"
            startAt = $startAt
            endAt = $null
            durationMinutes = 60
            inputGoal = "T08 restart smoke input"
            outcomeGoal = "T08 restart smoke outcome"
            relatedAppsOrSites = @(
                [ordered]@{ kind = "Application"; value = "notepad.exe" }
            )
            supervisionMode = "Interactive"
            reminderSettings = $null
        }
    } | ConvertTo-Json -Depth 8 -Compress
    $prepared = Send-CoreRequest -PipeName $pipeName -RequestJson $prepareJson
    if (-not $prepared.success -or -not $prepared.card.candidateId) {
        throw "IPC prepare failed: $($prepared.message)"
    }

    $confirmJson = [ordered]@{
        operation = "confirm"
        candidateId = $prepared.card.candidateId
    } | ConvertTo-Json -Depth 4 -Compress
    $confirmed = Send-CoreRequest -PipeName $pipeName -RequestJson $confirmJson
    if (-not $confirmed.success) {
        throw "IPC confirm failed: $($confirmed.message)"
    }

    $confirmedCommitments = @($confirmed.snapshot.commitments)
    if ($confirmedCommitments.Count -ne 1) {
        throw "Expected one confirmed commitment, found $($confirmedCommitments.Count)."
    }

    $confirmedId = [string]$confirmedCommitments[0].id
    $initialDesktops | Stop-Process -Force
    $initialDesktopExited = $initialDesktops[0].WaitForExit(5000)
    if (-not $initialDesktopExited) {
        throw "Initial Desktop did not exit after Stop-Process."
    }

    Stop-Process -Id $coreProcess.Id -Force
    $initialCoreExited = $coreProcess.WaitForExit(5000)
    if (-not $initialCoreExited) {
        throw "Initial Core did not exit after Stop-Process."
    }

    Start-Sleep -Milliseconds 500

    $restartedCore = Start-SmokeCore
    Start-Sleep -Seconds 4
    $restartedDesktops = Get-CoreDesktopProcesses -CoreProcessId $restartedCore.Id
    if ($restartedCore.HasExited) {
        throw "Restarted Core exited early with code $($restartedCore.ExitCode)."
    }

    if ($restartedDesktops.Count -ne 1) {
        throw "Expected one Desktop after Core restart, found $($restartedDesktops.Count)."
    }

    $ownedDesktopProcesses.Add($restartedDesktops[0])

    $snapshot = Send-CoreRequest -PipeName $pipeName -RequestJson '{"operation":"getSnapshot"}'
    if (-not $snapshot.success) {
        throw "Restart snapshot failed: $($snapshot.message)"
    }

    $recovered = @($snapshot.snapshot.commitments | Where-Object { [string]$_.id -eq $confirmedId })
    if ($recovered.Count -ne 1) {
        throw "Restarted Core did not recover commitment $confirmedId."
    }

    if ($recovered[0].inputGoal -ne "T08 restart smoke input" -or
        $recovered[0].outcomeGoal -ne "T08 restart smoke outcome" -or
        $recovered[0].phase -ne "Scheduled") {
        throw "Restarted Core recovered different commitment fields or phase."
    }

    $secondArguments = 'exec "{0}"' -f $desktopDll
    $secondDesktop = Start-Process -FilePath $DotnetPath -ArgumentList $secondArguments -WindowStyle Hidden -PassThru
    $secondExited = $secondDesktop.WaitForExit(5000)
    Start-Sleep -Milliseconds 500
    $desktopAfterSecondStart = Get-CoreDesktopProcesses -CoreProcessId $restartedCore.Id
    if (-not $secondExited) {
        throw "Second Desktop instance did not exit."
    }

    if ($desktopAfterSecondStart.Count -ne 1) {
        throw "Desktop single-instance failed; found $($desktopAfterSecondStart.Count) apphost processes."
    }

    $result = [ordered]@{
        InitialCorePid = $coreProcess.Id
        RestartedCorePid = $restartedCore.Id
        RestartedDesktopPid = $restartedDesktops[0].Id
        ConfirmedCommitmentId = $confirmedId
        RecoveredCommitmentId = [string]$recovered[0].id
        RecoveredPhase = [string]$recovered[0].phase
        SecondDesktopExited = $secondExited
        DesktopInstancesAfterSecondStart = $desktopAfterSecondStart.Count
        DatabaseCreated = Test-Path (Join-Path $smokeDirectory "jarvis.db")
    }
}
finally {
    foreach ($desktopProcess in $ownedDesktopProcesses) {
        if (-not $desktopProcess.HasExited) {
            Stop-Process -Id $desktopProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($process in @($secondDesktop, $restartedCore, $coreProcess)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($smokeDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $smokeDirectory).StartsWith("Jarvis-T08-Smoke-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$result.CleanupCompleted = -not (Test-Path -LiteralPath $smokeDirectory)
$result | ConvertTo-Json -Depth 4
