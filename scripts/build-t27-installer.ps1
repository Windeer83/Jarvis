param(
    [string]$DotnetPath = "",
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion = "0.1.0",
    [string]$InstallerOutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $mainCheckoutCandidate = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
    $workspaceRoot = Split-Path -Parent (Split-Path -Parent $repositoryRoot)
    $worktreeCandidate = Join-Path $workspaceRoot "Jarvis\.tools\dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $mainCheckoutCandidate) { $DotnetPath = $mainCheckoutCandidate }
    elseif (Test-Path -LiteralPath $worktreeCandidate) { $DotnetPath = $worktreeCandidate }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Resolve-Path $DotnetPath).Path

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$stageDirectory = Join-Path $artifactRoot "t27-publish"
$installerDirectory = if ([string]::IsNullOrWhiteSpace($InstallerOutputDirectory)) {
    Join-Path $artifactRoot "installer"
} else {
    [IO.Path]::GetFullPath($InstallerOutputDirectory)
}
function Is-SameOrChild([string]$Candidate, [string]$Root) {
    $candidateFull = [IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}
if (-not (Is-SameOrChild $artifactRoot $repositoryRoot)) {
    throw "Artifact directory escaped the repository."
}
if (-not (Is-SameOrChild $installerDirectory $artifactRoot)) {
    throw "Installer output directory must stay under the repository artifact directory."
}
if ([string]::Equals(
        $installerDirectory.TrimEnd('\', '/'),
        $artifactRoot.TrimEnd('\', '/'),
        [StringComparison]::OrdinalIgnoreCase) -or
    (Is-SameOrChild $installerDirectory $stageDirectory) -or
    (Is-SameOrChild $stageDirectory $installerDirectory)) {
    throw "Installer output directory must be a dedicated directory separate from the publish staging directory."
}
if (Test-Path -LiteralPath $stageDirectory) {
    Remove-Item -LiteralPath $stageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $installerDirectory) {
    Remove-Item -LiteralPath $installerDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDirectory,$installerDirectory | Out-Null

$publishArguments = @(
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=false", "-p:PublishReadyToRun=false",
    "-p:DebugType=None", "-p:DebugSymbols=false", "-o", $stageDirectory
)
$corePublishArguments = @("publish", "src\Jarvis.Core\Jarvis.Core.csproj") + $publishArguments
& $DotnetPath @corePublishArguments
if ($LASTEXITCODE -ne 0) { throw "Core self-contained publish failed." }
$desktopPublishArguments = @("publish", "src\Jarvis.Desktop\Jarvis.Desktop.csproj") + $publishArguments
& $DotnetPath @desktopPublishArguments
if ($LASTEXITCODE -ne 0) { throw "Desktop self-contained publish failed." }
Copy-Item -LiteralPath (Join-Path $repositoryRoot "installer\UNINSTALL-DATA-NOTICE.txt") `
    -Destination $stageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\apply-t28-maintenance.ps1") `
    -Destination $stageDirectory
Set-Content -LiteralPath (Join-Path $stageDirectory ".jarvis-program-root") `
    -Value "Jarvis installed program root" -Encoding ascii
Set-Content -LiteralPath (Join-Path $stageDirectory "installer-version.txt") `
    -Value $ProductVersion -Encoding ascii

$core = Join-Path $stageDirectory "Jarvis.Core.exe"
$desktop = Join-Path $stageDirectory "Jarvis.Desktop.exe"
if (-not (Test-Path -LiteralPath $core) -or -not (Test-Path -LiteralPath $desktop)) {
    throw "Self-contained publish did not contain both product executables."
}

function Get-WixIdentifier([string]$Prefix, [string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha256.ComputeHash($bytes) }
    finally { $sha256.Dispose() }
    $hex = ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    return $Prefix + $hex.Substring(0, 24)
}

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function Get-RelativePath([string]$BaseDirectory, [string]$Path) {
    $baseFullPath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\') + '\'
    $pathFullPath = [IO.Path]::GetFullPath($Path)
    $relativeUri = ([Uri]$baseFullPath).MakeRelativeUri([Uri]$pathFullPath)
    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace('/', '\')
}

function Write-WixDirectory([Text.StringBuilder]$Builder, [IO.DirectoryInfo]$Directory, [int]$Depth) {
    $relative = Get-RelativePath $stageDirectory $Directory.FullName
    $indent = " " * $Depth
    $id = Get-WixIdentifier "dir_" $relative
    [void]$Builder.AppendLine("$indent<Directory Id=`"$id`" Name=`"$(Escape-Xml $Directory.Name)`">")
    foreach ($child in @(Get-ChildItem -LiteralPath $Directory.FullName -Directory | Sort-Object Name)) {
        Write-WixDirectory $Builder $child ($Depth + 2)
    }
    [void]$Builder.AppendLine("$indent</Directory>")
}

$generatedPath = Join-Path $repositoryRoot "installer\GeneratedFiles.wxs"
$xml = [Text.StringBuilder]::new()
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$xml.AppendLine('  <Fragment>')
[void]$xml.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
foreach ($directory in @(Get-ChildItem -LiteralPath $stageDirectory -Directory | Sort-Object Name)) {
    Write-WixDirectory $xml $directory 6
}
[void]$xml.AppendLine('    </DirectoryRef>')
[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('  <Fragment>')
[void]$xml.AppendLine('    <ComponentGroup Id="PublishedFiles">')
foreach ($file in @(Get-ChildItem -LiteralPath $stageDirectory -File -Recurse |
        Where-Object Extension -ne '.pdb' |
        Sort-Object FullName)) {
    $relative = Get-RelativePath $stageDirectory $file.FullName
    $directoryName = [IO.Path]::GetDirectoryName($relative)
    $directoryId = if ([string]::IsNullOrEmpty($directoryName)) {
        'INSTALLFOLDER'
    } else {
        Get-WixIdentifier 'dir_' $directoryName
    }
    $componentId = Get-WixIdentifier 'cmp_' $relative
    $fileId = Get-WixIdentifier 'fil_' $relative
    $source = Escape-Xml $file.FullName
    [void]$xml.AppendLine("      <Component Id=`"$componentId`" Directory=`"$directoryId`" Guid=`"*`">")
    [void]$xml.AppendLine("        <File Id=`"$fileId`" Source=`"$source`" KeyPath=`"yes`" />")
    [void]$xml.AppendLine('      </Component>')
}
[void]$xml.AppendLine('    </ComponentGroup>')
[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('</Wix>')
[IO.File]::WriteAllText($generatedPath, $xml.ToString(), [Text.UTF8Encoding]::new($false))

& $DotnetPath build "installer\Jarvis.Installer.wixproj" -c Release `
    "-p:PublishDir=$stageDirectory" "-p:OutputPath=$installerDirectory" `
    "-p:ProductVersion=$ProductVersion"
if ($LASTEXITCODE -ne 0) { throw "WiX installer build failed." }

$expected = Join-Path $installerDirectory "Jarvis-$ProductVersion-win-x64.msi"
if (-not (Test-Path -LiteralPath $expected)) {
    $built = Get-ChildItem -LiteralPath $installerDirectory -Filter "*.msi" -File | Select-Object -First 1
    if (-not $built) { throw "WiX build did not produce an MSI." }
    Copy-Item -LiteralPath $built.FullName -Destination $expected
}

[pscustomobject]@{
    Installer = $expected
    SizeBytes = (Get-Item -LiteralPath $expected).Length
    Sha256 = (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash
    RuntimeIdentifier = "win-x64"
    SelfContained = $true
} | ConvertTo-Json -Depth 3
