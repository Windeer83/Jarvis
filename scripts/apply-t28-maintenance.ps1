param([Parameter(Mandatory = $true)][string]$RequestPath)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class JarvisCredentialErase
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    public static void DeleteIfPresent(string target)
    {
        if (CredDelete(target, 1, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error);
    }
}
'@

function Resolve-ExactPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Assert-SafeDirectory([string]$Path, [string]$MarkerName) {
    $full = Resolve-ExactPath $Path
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full.Length -le $root.Length + 3 -or $full -eq $env:USERPROFILE -or
        $full -eq $env:LOCALAPPDATA -or -not (Test-Path -LiteralPath (Join-Path $full $MarkerName))) {
        throw "Maintenance target failed the exact-root safety check."
    }
    return $full
}

function Is-SameOrChild([string]$Candidate, [string]$Root) {
    $candidateFull = (Resolve-ExactPath $Candidate) + '\'
    $rootFull = (Resolve-ExactPath $Root) + '\'
    return $candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function Wait-ForParent([int]$ParentProcessId) {
    if ($ParentProcessId -le 0) { return }
    $parent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
    if (-not $parent) { return }
    if (-not $parent.WaitForExit(120000)) {
        throw "Jarvis did not exit within the maintenance timeout."
    }
}

function Copy-RegisteredInstallerForRollback(
    [string]$ProgramDirectory,
    [string]$DestinationPath) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $roots = @(
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products",
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products"
    )
    $candidates = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($product in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $propertiesPath = Join-Path $product.PSPath "InstallProperties"
            if (-not (Test-Path -LiteralPath $propertiesPath)) { continue }
            $properties = Get-ItemProperty -LiteralPath $propertiesPath -ErrorAction SilentlyContinue
            if ($null -eq $properties -or [string]$properties.DisplayName -ne "Jarvis" -or
                [string]::IsNullOrWhiteSpace([string]$properties.LocalPackage)) { continue }
            if (-not (Test-Path -LiteralPath ([string]$properties.LocalPackage))) { continue }
            $candidates += [pscustomobject]@{
                InstallLocation = [string]$properties.InstallLocation
                LocalPackage = [string]$properties.LocalPackage
            }
        }
    }
    $match = @($candidates | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.InstallLocation) -and
        (Resolve-ExactPath $_.InstallLocation) -eq (Resolve-ExactPath $ProgramDirectory)
    } | Select-Object -First 1)
    if ($match.Count -eq 0 -and $candidates.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace($candidates[0].InstallLocation)) {
        $match = @($candidates[0])
    }
    if ($match.Count -eq 1) {
        Copy-Item -LiteralPath $match[0].LocalPackage -Destination $DestinationPath -Force
        return $DestinationPath
    }
    return $null
}

function Write-Status(
    [string]$StatusPath,
    [string]$Operation,
    [string]$Status,
    [bool]$RollbackApplied,
    [int]$InstallerExitCode,
    [int]$HealthExitCode,
    [string]$ManualRecoveryDirectory) {
    $payload = [ordered]@{
        format = "jarvis-maintenance-status"
        version = 1
        operation = $Operation
        status = $Status
        at = [DateTimeOffset]::UtcNow.ToString("O")
        rollbackApplied = $RollbackApplied
        installerExitCode = $InstallerExitCode
        healthExitCode = $HealthExitCode
        manualRecoveryDirectory = $ManualRecoveryDirectory
        note = "No credentials, private content, activity titles, chat text or backup password are recorded."
    }
    $directory = Split-Path -Parent $StatusPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText(
        $StatusPath,
        ($payload | ConvertTo-Json -Depth 3),
        [Text.UTF8Encoding]::new($false))
}

$requestFullPath = (Resolve-Path -LiteralPath $RequestPath).Path
$request = Get-Content -LiteralPath $requestFullPath -Raw -Encoding UTF8 | ConvertFrom-Json
$operation = [string]$request.operation
$work = Resolve-ExactPath ([string]$request.workingDirectory)
$maintenanceRoot = Assert-SafeDirectory ([string]$request.maintenanceRoot) ".jarvis-maintenance-root"
if (-not (Is-SameOrChild $work $maintenanceRoot)) {
    throw "Maintenance work directory escaped the verified maintenance root."
}
$statusPath = Resolve-ExactPath ([string]$request.statusPath)
Wait-ForParent ([int]$request.parentProcessId)

if ($operation -ieq "ProductUpdate") {
    $program = Assert-SafeDirectory ([string]$request.programDirectory) ".jarvis-program-root"
    $data = Assert-SafeDirectory ([string]$request.dataDirectory) ".jarvis-data-root"
    if (-not (Is-SameOrChild $statusPath $data)) { throw "Update status path escaped the verified data root." }
    $installer = (Resolve-Path -LiteralPath ([string]$request.installerPath)).Path
    $rollbackDatabase = (Resolve-Path -LiteralPath ([string]$request.databaseRollbackPath)).Path
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne [string]$request.installerSha256) {
        throw "Installer changed after confirmation."
    }
    $programRollback = Join-Path $work "program-rollback"
    $previousInstaller = Copy-RegisteredInstallerForRollback `
        $program (Join-Path $work "previous-installer.msi")
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $startupWasEnabled = $null -ne (Get-ItemProperty -Path $runKey `
        -Name ([string]$request.loginStartupValueName) -ErrorAction SilentlyContinue)
    if (Test-Path -LiteralPath $programRollback) { Remove-Item -LiteralPath $programRollback -Recurse -Force }
    New-Item -ItemType Directory -Path $programRollback | Out-Null
    Copy-Item -Path (Join-Path $program '*') -Destination $programRollback -Recurse -Force
    $installerExit = -1
    $healthExit = -1
    try {
        $installArguments = @(
            "/i", ('"' + $installer + '"'), "/qn", "/norestart",
            ('INSTALLFOLDER="' + $program + '\"'))
        if ($request.installMainProgramOnly -eq $true) { $installArguments += "ADDLOCAL=MainProgram" }
        $install = Start-Process msiexec.exe -ArgumentList $installArguments -Wait -PassThru
        $installerExit = $install.ExitCode
        if ($installerExit -ne 0) { throw "Installer returned a failure code." }
        $core = Join-Path $program "Jarvis.Core.exe"
        $desktop = Join-Path $program "Jarvis.Desktop.exe"
        if (-not (Test-Path -LiteralPath $core) -or -not (Test-Path -LiteralPath $desktop)) {
            throw "Updated program files are incomplete."
        }
        $health = Start-Process -FilePath $core -ArgumentList @(
            "--health-check", "--data-dir", ('"' + $data + '"')) -Wait -PassThru -WindowStyle Hidden
        $healthExit = $health.ExitCode
        if ($healthExit -ne 0) { throw "Updated Core/database health check failed." }
        Write-Status $statusPath $operation "completed" $false $installerExit $healthExit $null
        Remove-Item -LiteralPath $programRollback -Recurse -Force
        Remove-Item -LiteralPath $rollbackDatabase -Force
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        if ($request.restartAfterMaintenance -ne $false) {
            Start-Process -FilePath $core -WindowStyle Hidden | Out-Null
        }
    }
    catch {
        $database = Resolve-ExactPath ([string]$request.databasePath)
        if (-not (Is-SameOrChild $database $data)) { throw "Database path escaped the verified data root." }
        $rollbackTemporary = $database + ".rollback.tmp"
        Copy-Item -LiteralPath $rollbackDatabase -Destination $rollbackTemporary -Force
        Remove-Item -LiteralPath ($database + "-wal"),($database + "-shm"),($database + "-journal") `
            -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $rollbackTemporary -Destination $database -Force
        Remove-Item -LiteralPath ($database + "-wal"),($database + "-shm"),($database + "-journal") `
            -Force -ErrorAction SilentlyContinue
        $registrationRestored = $installerExit -ne 0
        if ($installerExit -eq 0 -and -not [string]::IsNullOrWhiteSpace($previousInstaller)) {
            $remove = Start-Process msiexec.exe -ArgumentList @(
                "/x", ('"' + $installer + '"'), "/qn", "/norestart") -Wait -PassThru
            $features = if ($startupWasEnabled) { "MainProgram,AutoStart" } else { "MainProgram" }
            if ($remove.ExitCode -eq 0) {
                $restoreRegistration = Start-Process msiexec.exe -ArgumentList @(
                    "/i", ('"' + $previousInstaller + '"'), "/qn", "/norestart",
                    ("ADDLOCAL=" + $features), ('INSTALLFOLDER="' + $program + '\"')) -Wait -PassThru
                $registrationRestored = $restoreRegistration.ExitCode -eq 0
            }
        }
        Get-ChildItem -LiteralPath $program -Force | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $programRollback '*') -Destination $program -Recurse -Force
        Write-Status $statusPath $operation `
            $(if ($registrationRestored) { "rolled_back" } else { "rollback_incomplete" }) `
            $true $installerExit $healthExit $work
        $oldCore = Join-Path $program "Jarvis.Core.exe"
        if ($request.restartAfterMaintenance -ne $false -and (Test-Path -LiteralPath $oldCore)) {
            Start-Process -FilePath $oldCore -WindowStyle Hidden | Out-Null
        }
        exit $(if ($registrationRestored) { 2 } else { 3 })
    }
    exit 0
}

if ($operation -ieq "SafeErase") {
    if (-not (Is-SameOrChild $statusPath $work)) { throw "Erase status path escaped the maintenance directory." }
    $data = Assert-SafeDirectory ([string]$request.dataDirectory) ".jarvis-data-root"
    $finalBackup = (Resolve-Path -LiteralPath ([string]$request.finalBackupPath)).Path
    if ((Is-SameOrChild $finalBackup $data) -or
        (Is-SameOrChild $finalBackup $maintenanceRoot)) {
        throw "Final backup is inside the erase scope."
    }
    $configuredBackup = [string]$request.configuredBackupDirectory
    if (-not [string]::IsNullOrWhiteSpace($configuredBackup)) {
        $configuredBackup = Resolve-ExactPath $configuredBackup
        if (Is-SameOrChild $finalBackup $configuredBackup) { throw "Final backup is inside the backup erase scope." }
        if (Test-Path -LiteralPath $configuredBackup) {
            Get-ChildItem -LiteralPath $configuredBackup -File -Filter "jarvis-*.jarvis-backup" |
                Where-Object FullName -NE $finalBackup |
                Remove-Item -Force
            Get-ChildItem -LiteralPath $configuredBackup -File -Filter "jarvis-*.jarvis-backup.tmp" |
                Remove-Item -Force
        }
    }
    foreach ($target in @($request.credentialTargets)) {
        if ([string]$target -notmatch '^Jarvis/') { throw "Credential target escaped the Jarvis namespace." }
        [JarvisCredentialErase]::DeleteIfPresent([string]$target)
    }
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name ([string]$request.loginStartupValueName) -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $data -Recurse -Force
    Write-Status $statusPath $operation "completed" $false 0 0 $finalBackup
    Remove-Item -LiteralPath $maintenanceRoot -Recurse -Force
    exit 0
}

throw "Unknown maintenance operation."
