param(
    [string]$CorePath = "$env:LOCALAPPDATA\Programs\Jarvis\Jarvis.Core.exe",
    [string]$BluetoothInterfaceAlias = "",
    [switch]$Preview
)

$ErrorActionPreference = "Stop"
$ruleDisplayName = "Jarvis Mobile Sync (Bluetooth PAN)"
$CorePath = [IO.Path]::GetFullPath($CorePath)

if (-not (Test-Path -LiteralPath $CorePath)) {
    throw "Jarvis Core was not found at: $CorePath"
}

if ([string]::IsNullOrWhiteSpace($BluetoothInterfaceAlias)) {
    $adapter = Get-NetAdapter -IncludeHidden |
        Where-Object { $_.InterfaceDescription -match "Bluetooth.*Personal Area Network" } |
        Select-Object -First 1
    if ($null -eq $adapter) {
        throw "No Windows Bluetooth Personal Area Network adapter was found."
    }
    $BluetoothInterfaceAlias = $adapter.Name
} else {
    $adapter = Get-NetAdapter -InterfaceAlias $BluetoothInterfaceAlias -ErrorAction Stop
}
if ($adapter.Status -ne "Up") {
    throw "Bluetooth PAN is not connected. Join the phone PAN before configuring the firewall."
}

$generatedRules = @(
    Get-NetFirewallApplicationFilter -Program $CorePath -ErrorAction SilentlyContinue |
        ForEach-Object { $_ | Get-NetFirewallRule } |
        Where-Object {
            $_.Direction -eq "Inbound" -and
            $_.Action -eq "Allow" -and
            $_.Profile -match "Public" -and
            $_.DisplayName -eq "jarvis.core.exe" -and
            $_.Name -match "^(TCP|UDP) Query User"
        }
)

Write-Host "Jarvis Core: $CorePath"
Write-Host "Bluetooth PAN interface: $BluetoothInterfaceAlias"
Write-Host "Windows-generated broad rules to remove: $($generatedRules.Count)"
Write-Host "Replacement: inbound TCP 42731, Public profile, Bluetooth PAN only, remote LocalSubnet."

if ($Preview) {
    $generatedRules | Select-Object Name, DisplayName, Enabled, Direction, Action, Profile | Format-Table
    Write-Host "Preview only; no firewall rule was changed."
    return
}

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    throw "Run PowerShell as administrator, then run this script again."
}

Get-NetFirewallRule -DisplayName $ruleDisplayName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
$generatedRules | Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName $ruleDisplayName `
    -Direction Inbound `
    -Action Allow `
    -Program $CorePath `
    -Protocol TCP `
    -LocalPort 42731 `
    -Profile Public `
    -InterfaceAlias $BluetoothInterfaceAlias `
    -RemoteAddress LocalSubnet `
    -EdgeTraversalPolicy Block | Out-Null

$created = Get-NetFirewallRule -DisplayName $ruleDisplayName -ErrorAction Stop
$interface = $created | Get-NetFirewallInterfaceFilter
$port = $created | Get-NetFirewallPortFilter
$address = $created | Get-NetFirewallAddressFilter
[pscustomobject]@{
    DisplayName = $created.DisplayName
    Enabled = $created.Enabled
    Direction = $created.Direction
    Action = $created.Action
    Profile = $created.Profile
    InterfaceAlias = $interface.InterfaceAlias -join ","
    Protocol = $port.Protocol
    LocalPort = $port.LocalPort -join ","
    RemoteAddress = $address.RemoteAddress -join ","
} | Format-List

Write-Host "Jarvis mobile sync firewall scope is now limited to Bluetooth PAN TCP 42731."
