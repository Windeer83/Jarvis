$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$worker = Join-Path $repositoryRoot "scripts\apply-t28-maintenance.ps1"
$tempRoot = [IO.Path]::GetFullPath($env:TEMP)
$validationRoot = Join-Path $tempRoot ("Jarvis-T28-Maintenance-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $validationRoot | Out-Null

function Write-Request([string]$Path, [hashtable]$Value) {
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
}

try {
    $updateRoot = Join-Path $validationRoot "update"
    $program = Join-Path $updateRoot "program"
    $data = Join-Path $updateRoot "data"
    $updateMaintenance = Join-Path $updateRoot "maintenance"
    $work = Join-Path $updateMaintenance "update"
    New-Item -ItemType Directory -Path $program,$data,$updateMaintenance,$work | Out-Null
    Set-Content -LiteralPath (Join-Path $updateMaintenance ".jarvis-maintenance-root") -Value "marker"
    Set-Content -LiteralPath (Join-Path $program ".jarvis-program-root") -Value "marker"
    Set-Content -LiteralPath (Join-Path $program "Jarvis.Core.exe") -Value "old-core"
    Set-Content -LiteralPath (Join-Path $program "old-program-sentinel.txt") -Value "old-program"
    Set-Content -LiteralPath (Join-Path $data ".jarvis-data-root") -Value "marker"
    $database = Join-Path $data "jarvis.db"
    Set-Content -LiteralPath $database -Value "new-database"
    Set-Content -LiteralPath ($database + "-wal") -Value "stale-new-wal"
    Set-Content -LiteralPath ($database + "-shm") -Value "stale-new-shm"
    Set-Content -LiteralPath ($database + "-journal") -Value "stale-new-journal"
    $rollback = Join-Path $work "database-rollback.sqlite3"
    Set-Content -LiteralPath $rollback -Value "old-database"
    $badInstaller = Join-Path $updateRoot "invalid.msi"
    Set-Content -LiteralPath $badInstaller -Value "not-an-msi"
    $status = Join-Path $data "last-maintenance-status.json"
    $requestPath = Join-Path $work "request.json"
    Write-Request $requestPath ([ordered]@{
        operation = "ProductUpdate"
        requestId = [Guid]::NewGuid()
        parentProcessId = 0
        workingDirectory = $work
        maintenanceRoot = $updateMaintenance
        programDirectory = $program
        dataDirectory = $data
        databasePath = $database
        installerPath = $badInstaller
        installerSha256 = (Get-FileHash -LiteralPath $badInstaller -Algorithm SHA256).Hash
        databaseRollbackPath = $rollback
        configuredBackupDirectory = $null
        finalBackupPath = $null
        credentialTargets = @()
        loginStartupValueName = "Jarvis T28 Validation"
        statusPath = $status
        createdAt = [DateTimeOffset]::UtcNow
        restartAfterMaintenance = $false
        installMainProgramOnly = $true
    })
    $rollbackError = Join-Path $updateRoot "worker-error.txt"
    $rollbackRun = Start-Process powershell.exe -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $worker + '"'),
        "-RequestPath", ('"' + $requestPath + '"')) -Wait -PassThru `
        -RedirectStandardError $rollbackError
    if ($rollbackRun.ExitCode -ne 2) {
        $errorText = Get-Content -LiteralPath $rollbackError -Raw -ErrorAction SilentlyContinue
        throw "Injected update failure returned $($rollbackRun.ExitCode), expected rollback exit code 2. $errorText"
    }
    $rollbackStatus = Get-Content -LiteralPath $status -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($rollbackStatus.status -ne "rolled_back" -or -not $rollbackStatus.rollbackApplied) {
        throw "Update failure did not record a completed rollback."
    }
    if ((Get-Content -LiteralPath $database -Raw).Trim() -ne "old-database") {
        throw "Database rollback did not restore the pre-upgrade snapshot."
    }
    if ((Test-Path -LiteralPath ($database + "-wal")) -or
        (Test-Path -LiteralPath ($database + "-shm")) -or
        (Test-Path -LiteralPath ($database + "-journal"))) {
        throw "Database rollback retained WAL/SHM/journal files from the failed version."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $program "old-program-sentinel.txt"))) {
        throw "Program rollback did not restore the previous program tree."
    }

    $eraseRoot = Join-Path $validationRoot "erase"
    $eraseData = Join-Path $eraseRoot "data"
    $eraseBackups = Join-Path $eraseRoot "configured-backups"
    $eraseMaintenance = Join-Path $eraseRoot "maintenance"
    $eraseWork = Join-Path $eraseMaintenance "erase"
    $external = Join-Path $eraseRoot "external"
    New-Item -ItemType Directory -Path $eraseData,$eraseBackups,$eraseMaintenance,$eraseWork,$external | Out-Null
    Set-Content -LiteralPath (Join-Path $eraseMaintenance ".jarvis-maintenance-root") -Value "marker"
    Set-Content -LiteralPath (Join-Path $eraseData ".jarvis-data-root") -Value "marker"
    Set-Content -LiteralPath (Join-Path $eraseData "private.db") -Value "private"
    Set-Content -LiteralPath (Join-Path $eraseBackups "jarvis-daily-test.jarvis-backup") -Value "backup"
    Set-Content -LiteralPath (Join-Path $eraseBackups "keep-unrelated.txt") -Value "keep"
    $final = Join-Path $external "jarvis-final.jarvis-backup"
    Set-Content -LiteralPath $final -Value "final"
    $eraseStatus = Join-Path $eraseWork "status.json"
    $eraseRequest = Join-Path $eraseWork "request.json"
    Write-Request $eraseRequest ([ordered]@{
        operation = "SafeErase"
        requestId = [Guid]::NewGuid()
        parentProcessId = 0
        workingDirectory = $eraseWork
        maintenanceRoot = $eraseMaintenance
        programDirectory = ""
        dataDirectory = $eraseData
        databasePath = (Join-Path $eraseData "jarvis.db")
        installerPath = $null
        installerSha256 = $null
        databaseRollbackPath = $null
        configuredBackupDirectory = $eraseBackups
        finalBackupPath = $final
        credentialTargets = @()
        loginStartupValueName = "Jarvis T28 Validation"
        statusPath = $eraseStatus
        createdAt = [DateTimeOffset]::UtcNow
        restartAfterMaintenance = $false
        installMainProgramOnly = $true
    })
    $eraseRun = Start-Process powershell.exe -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $worker + '"'),
        "-RequestPath", ('"' + $eraseRequest + '"')) -Wait -PassThru
    if ($eraseRun.ExitCode -ne 0) { throw "Safe erase worker failed with $($eraseRun.ExitCode)." }
    if (Test-Path -LiteralPath $eraseData) { throw "Safe erase retained the verified Jarvis data root." }
    if (Test-Path -LiteralPath $eraseMaintenance) { throw "Safe erase retained Jarvis maintenance logs." }
    if (Test-Path -LiteralPath (Join-Path $eraseBackups "jarvis-daily-test.jarvis-backup")) {
        throw "Safe erase retained a configured Jarvis backup."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $eraseBackups "keep-unrelated.txt")) -or
        -not (Test-Path -LiteralPath $final)) {
        throw "Safe erase crossed its declared scope or removed the external final backup."
    }

    [pscustomobject]@{
        UpdateFailureRolledBackProgram = $true
        UpdateFailureRolledBackDatabase = $true
        UpdateFailureRemovedStaleWal = $true
        SafeEraseRemovedData = $true
        SafeEraseRemovedMaintenanceLogs = $true
        SafeEraseRemovedJarvisBackups = $true
        UnrelatedFilePreserved = $true
        ExternalFinalBackupPreserved = $true
    } | ConvertTo-Json
}
finally {
    if ($validationRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $validationRoot).StartsWith("Jarvis-T28-Maintenance-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
