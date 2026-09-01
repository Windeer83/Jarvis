$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$oldOutput = Join-Path $artifactRoot "t28-old-installer"
$newOutput = Join-Path $artifactRoot "t28-new-installer"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-t27-installer.ps1") `
    -ProductVersion 0.1.0 -InstallerOutputDirectory $oldOutput
if ($LASTEXITCODE -ne 0) { throw "Failed to build the old installer fixture." }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-t27-installer.ps1") `
    -ProductVersion 0.1.1 -InstallerOutputDirectory $newOutput
if ($LASTEXITCODE -ne 0) { throw "Failed to build the new installer fixture." }
$oldInstaller = Join-Path $oldOutput "Jarvis-0.1.0-win-x64.msi"
$newInstaller = Join-Path $newOutput "Jarvis-0.1.1-win-x64.msi"
$worker = Join-Path $repositoryRoot "scripts\apply-t28-maintenance.ps1"
$tempRoot = [IO.Path]::GetFullPath($env:TEMP)
$validationRoot = Join-Path $tempRoot ("Jarvis-T28-Update-" + [Guid]::NewGuid().ToString("N"))
$program = Join-Path $validationRoot "program"
$data = Join-Path $validationRoot "data"
$maintenance = Join-Path $validationRoot "maintenance"
$work = Join-Path $maintenance "update"
New-Item -ItemType Directory -Path $program,$data,$maintenance,$work | Out-Null
Set-Content -LiteralPath (Join-Path $maintenance ".jarvis-maintenance-root") -Value "marker"
$scopeWasSet = Test-Path Env:JARVIS_INSTANCE_SCOPE
$oldScope = $env:JARVIS_INSTANCE_SCOPE
$env:JARVIS_INSTANCE_SCOPE = "t28_" + [Guid]::NewGuid().ToString("N")
$installed = $false
$core = $null

try {
    $install = Start-Process msiexec.exe -ArgumentList @(
        "/i", ('"' + $oldInstaller + '"'), "/qn", "/norestart", "ADDLOCAL=MainProgram",
        ('INSTALLFOLDER="' + $program + '\"')) -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Old fixture install failed with $($install.ExitCode)." }
    $installed = $true
    $corePath = Join-Path $program "Jarvis.Core.exe"
    $desktopPath = Join-Path $program "Jarvis.Desktop.exe"
    $core = Start-Process -FilePath $corePath -ArgumentList @(
        "--data-dir", ('"' + $data + '"'), "--desktop-path", ('"' + $desktopPath + '"')) `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 4
    if ($core.HasExited) { throw "Old Core failed to initialize the update fixture." }
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($core.Id)" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    $core.WaitForExit(5000) | Out-Null
    $core = $null
    $database = Join-Path $data "jarvis.db"
    $rollback = Join-Path $work "database-rollback.sqlite3"
    Copy-Item -LiteralPath $database -Destination $rollback
    $status = Join-Path $data "last-maintenance-status.json"
    $request = [ordered]@{
        operation = "ProductUpdate"
        requestId = [Guid]::NewGuid()
        parentProcessId = 0
        workingDirectory = $work
        maintenanceRoot = $maintenance
        programDirectory = $program
        dataDirectory = $data
        databasePath = $database
        installerPath = $newInstaller
        installerSha256 = (Get-FileHash -LiteralPath $newInstaller -Algorithm SHA256).Hash
        databaseRollbackPath = $rollback
        configuredBackupDirectory = $null
        finalBackupPath = $null
        credentialTargets = @()
        loginStartupValueName = "Jarvis T28 Update Validation"
        statusPath = $status
        createdAt = [DateTimeOffset]::UtcNow
        restartAfterMaintenance = $false
        installMainProgramOnly = $true
    }
    $requestPath = Join-Path $work "request.json"
    [IO.File]::WriteAllText(
        $requestPath, ($request | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $updated = Start-Process powershell.exe -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $worker + '"'),
        "-RequestPath", ('"' + $requestPath + '"')) -Wait -PassThru
    if ($updated.ExitCode -ne 0) { throw "Update worker failed with $($updated.ExitCode)." }
    $result = Get-Content -LiteralPath $status -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($result.status -ne "completed" -or $result.rollbackApplied) {
        throw "Successful update did not record a clean health-checked completion."
    }
    if (-not (Test-Path -LiteralPath $corePath) -or -not (Test-Path -LiteralPath $desktopPath)) {
        throw "Updated program tree is incomplete."
    }
    if ((Get-Content -LiteralPath (Join-Path $program "installer-version.txt") -Raw).Trim() -ne "0.1.1") {
        throw "Successful update did not replace the installed program version marker."
    }
    $uninstall = Start-Process msiexec.exe -ArgumentList @(
        "/x", ('"' + $newInstaller + '"'), "/qn", "/norestart") -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Updated fixture uninstall failed." }
    $installed = $false

    New-Item -ItemType Directory -Path $program -Force | Out-Null
    $installAgain = Start-Process msiexec.exe -ArgumentList @(
        "/i", ('"' + $oldInstaller + '"'), "/qn", "/norestart", "ADDLOCAL=MainProgram",
        ('INSTALLFOLDER="' + $program + '\"')) -Wait -PassThru
    if ($installAgain.ExitCode -ne 0) { throw "Rollback fixture reinstall failed." }
    $installed = $true
    $core = Start-Process -FilePath $corePath -ArgumentList @(
        "--data-dir", ('"' + $data + '"'), "--desktop-path", ('"' + $desktopPath + '"')) `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 4
    if ($core.HasExited) { throw "Rollback fixture Core failed to start." }
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($core.Id)" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    $core.WaitForExit(5000) | Out-Null
    $core = $null
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Copy-Item -LiteralPath $database -Destination $rollback -Force
    $rollbackHash = (Get-FileHash -LiteralPath $rollback -Algorithm SHA256).Hash
    Set-Content -LiteralPath $database -Value "corrupt database to force post-install health failure"
    Remove-Item -LiteralPath $status -Force -ErrorAction SilentlyContinue
    $request.requestId = [Guid]::NewGuid()
    $request.createdAt = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText(
        $requestPath, ($request | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $rolledBack = Start-Process powershell.exe -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $worker + '"'),
        "-RequestPath", ('"' + $requestPath + '"')) -Wait -PassThru
    if ($rolledBack.ExitCode -ne 2) { throw "Post-install health failure did not trigger rollback." }
    $rollbackResult = Get-Content -LiteralPath $status -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($rollbackResult.status -ne "rolled_back" -or -not $rollbackResult.rollbackApplied) {
        throw "Post-install health failure did not record rollback."
    }
    if ((Get-Content -LiteralPath (Join-Path $program "installer-version.txt") -Raw).Trim() -ne "0.1.0") {
        throw "Program rollback did not restore the old version marker."
    }
    if ((Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -ne $rollbackHash) {
        throw "Post-install health failure did not restore the database snapshot."
    }
    $uninstallRollback = Start-Process msiexec.exe -ArgumentList @(
        "/x", ('"' + $oldInstaller + '"'), "/qn", "/norestart") -Wait -PassThru
    if ($uninstallRollback.ExitCode -ne 0) {
        throw "Program files rolled back, but Windows Installer registration did not return to the old version."
    }
    $installed = $false
    [pscustomobject]@{
        ManualUpdateCompleted = $true
        ProgramFilesHealthy = $true
        DatabaseHealthy = $true
        RollbackNotNeeded = $true
        PostInstallHealthFailureRolledBackProgram = $true
        PostInstallHealthFailureRolledBackDatabase = $true
        PostInstallHealthFailureRolledBackInstallerRegistration = $true
        UninstallCompleted = $true
    } | ConvertTo-Json
}
finally {
    if ($core -and -not $core.HasExited) { Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue }
    if ($installed) {
        Start-Process msiexec.exe -ArgumentList @(
            "/x", ('"' + $newInstaller + '"'), "/qn", "/norestart") -Wait | Out-Null
        Start-Process msiexec.exe -ArgumentList @(
            "/x", ('"' + $oldInstaller + '"'), "/qn", "/norestart") -Wait | Out-Null
    }
    if ($scopeWasSet) { $env:JARVIS_INSTANCE_SCOPE = $oldScope }
    else { Remove-Item Env:JARVIS_INSTANCE_SCOPE -ErrorAction SilentlyContinue }
    if ($validationRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $validationRoot).StartsWith("Jarvis-T28-Update-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
