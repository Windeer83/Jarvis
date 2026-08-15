param(
    [string]$InstallerPath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repositoryRoot "artifacts\installer\Jarvis-0.1.0-win-x64.msi"
}

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "T27 installer not found: $InstallerPath"
}

$installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "installer\Package.wxs") -Raw -Encoding utf8
$requiredAuthoring = @(
    'Scope="perUser"',
    'WINDOWSBUILDNUMBER &gt;= 22000',
    'Id="AutoStart"',
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'Id="LocalFirstNotice"',
    'BitLocker',
    'Id="UninstallRetentionNotice"'
)
foreach ($required in $requiredAuthoring) {
    if ($installerSource.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Installer authoring is missing required boundary: $required"
    }
}

$tempRoot = [IO.Path]::GetFullPath($env:TEMP)
$validationRoot = Join-Path $tempRoot ("Jarvis-T27-Package-" + [Guid]::NewGuid().ToString("N"))
$installDirectory = Join-Path $validationRoot "program"
$dataDirectory = Join-Path $validationRoot "preserved-data"
$logDirectory = Join-Path $validationRoot "logs"
New-Item -ItemType Directory -Path $installDirectory,$dataDirectory,$logDirectory | Out-Null
$sentinel = Join-Path $dataDirectory "must-survive-uninstall.txt"
Set-Content -LiteralPath $sentinel -Value "preserve" -Encoding utf8
$installLog = Join-Path $logDirectory "install.log"
$uninstallLog = Join-Path $logDirectory "uninstall.log"
$scopeWasSet = Test-Path Env:JARVIS_INSTANCE_SCOPE
$oldScope = $env:JARVIS_INSTANCE_SCOPE
$env:JARVIS_INSTANCE_SCOPE = "t27_" + [Guid]::NewGuid().ToString("N")
$core = $null
$desktopProcesses = @()
$installed = $false

try {
    $install = Start-Process msiexec.exe -ArgumentList @(
        "/i", ('"' + (Resolve-Path $InstallerPath).Path + '"'),
        "/qn", "/norestart", "ADDLOCAL=MainProgram",
        ('INSTALLFOLDER="' + $installDirectory + '\"'),
        "/l*v", ('"' + $installLog + '"')) -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "MSI install failed with exit code $($install.ExitCode). See $installLog"
    }
    $installed = $true

    $corePath = Join-Path $installDirectory "Jarvis.Core.exe"
    $desktopPath = Join-Path $installDirectory "Jarvis.Desktop.exe"
    if (-not (Test-Path -LiteralPath $corePath) -or -not (Test-Path -LiteralPath $desktopPath)) {
        throw "Self-contained Core/Desktop executables were not installed."
    }

    $core = Start-Process -FilePath $corePath -ArgumentList @(
        "--data-dir", ('"' + $dataDirectory + '"'),
        "--desktop-path", ('"' + $desktopPath + '"')) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    if ($core.HasExited) {
        throw "Installed Core exited early with code $($core.ExitCode)."
    }
    $desktopProcesses = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($core.Id) AND Name = 'Jarvis.Desktop.exe'" |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($desktopProcesses.Count -ne 1) {
        throw "Installed Core did not start exactly one Desktop process."
    }

    $desktopProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    $core.WaitForExit(5000) | Out-Null
    $core = $null

    $uninstall = Start-Process msiexec.exe -ArgumentList @(
        "/x", ('"' + (Resolve-Path $InstallerPath).Path + '"'),
        "/qn", "/norestart", "/l*v", ('"' + $uninstallLog + '"')) -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "MSI uninstall failed with exit code $($uninstall.ExitCode). See $uninstallLog"
    }
    $installed = $false
    if (Test-Path -LiteralPath $corePath) {
        throw "Program files remained after ordinary uninstall."
    }
    if (-not (Test-Path -LiteralPath $sentinel)) {
        throw "Ordinary uninstall removed user data outside the program directory."
    }

    [pscustomobject]@{
        Installer = (Resolve-Path $InstallerPath).Path
        InstallerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
        InstalledCore = $true
        InstalledDesktop = $true
        DesktopInstances = 1
        ProgramRemoved = $true
        UserDataPreserved = $true
    } | ConvertTo-Json -Depth 3
}
finally {
    foreach ($desktop in $desktopProcesses) {
        if ($desktop -and -not $desktop.HasExited) {
            Stop-Process -Id $desktop.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($core -and -not $core.HasExited) {
        Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    }
    if ($installed) {
        Start-Process msiexec.exe -ArgumentList @(
            "/x", ('"' + $InstallerPath + '"'), "/qn", "/norestart") -Wait | Out-Null
    }
    if ($scopeWasSet) { $env:JARVIS_INSTANCE_SCOPE = $oldScope }
    else { Remove-Item Env:JARVIS_INSTANCE_SCOPE -ErrorAction SilentlyContinue }
    if ($validationRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $validationRoot).StartsWith("Jarvis-T27-Package-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
