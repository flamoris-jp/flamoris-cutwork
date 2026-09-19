[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64",

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\Cutwork.App\Cutwork.App.csproj"
$publishProfilePath = Join-Path $repositoryRoot "src\Cutwork.App\Properties\PublishProfiles\win-x64.pubxml"
$publishPath = Join-Path $repositoryRoot "artifacts\publish\win-x64"
$packageName = "FLAMORIS-Cutwork-v$Version-win-x64"
$packageRoot = Join-Path $repositoryRoot "artifacts\package\$packageName"
$zipPath = Join-Path $repositoryRoot "artifacts\$packageName.zip"
$inventoryPath = Join-Path $repositoryRoot "artifacts\$packageName.inventory.json"

[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
[xml]$profileXml = Get-Content -LiteralPath $publishProfilePath -Raw
$projectVersion = [string]$projectXml.Project.PropertyGroup.VersionPrefix
$profileRuntime = [string]$profileXml.Project.PropertyGroup.RuntimeIdentifier
$profileSelfContained = [string]$profileXml.Project.PropertyGroup.SelfContained
$profileConfiguration = [string]$profileXml.Project.PropertyGroup.Configuration
$profileCopySymbols = [string]$profileXml.Project.PropertyGroup.CopyOutputSymbolsToPublishDirectory
$profileLanguages = [string]$profileXml.Project.PropertyGroup.SatelliteResourceLanguages
if ($projectVersion -cne $Version) {
    throw "Package version $Version does not match project VersionPrefix $projectVersion."
}
if ($profileRuntime -cne $RuntimeIdentifier -or $profileSelfContained -cne "true" -or
    $profileConfiguration -cne $Configuration -or $profileCopySymbols -cne "false" -or
    $profileLanguages -cne "ja;en") {
    throw "Publish profile must remain Release, win-x64, self-contained, JA/EN-only, and free of debug symbols."
}

function Remove-ExactPath {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

Remove-ExactPath -Path $publishPath
Remove-ExactPath -Path $packageRoot
Remove-ExactPath -Path $zipPath
Remove-ExactPath -Path $inventoryPath

Write-Host "Publishing Cutwork ($Configuration, $RuntimeIdentifier, self-contained)..."
& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    /p:PublishProfile=win-x64

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$bridgeProject = Join-Path $repositoryRoot "src\Cutwork.Bridge\Cutwork.Bridge.csproj"
$bridgePath = Join-Path $publishPath "mcp"
& dotnet publish $bridgeProject -c Release -r win-x64 --self-contained true -o $bridgePath /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Bridge publish failed." }
if (-not (Test-Path (Join-Path $bridgePath "Cutwork.Bridge.exe"))) { throw "Bridge missing." }
if (-not (Test-Path (Join-Path $bridgePath "Flamoris.Logging.dll"))) { throw "Bridge logging dependency missing." }
Copy-Item (Join-Path $repositoryRoot "packaging\MCP-SDK-LICENSE.txt") (Join-Path $publishPath "MCP-SDK-LICENSE.txt")

# Referenced projects can still contribute PDBs even when the app publish profile
# disables symbol copying. They are not required to run the portable release.
Get-ChildItem -LiteralPath $publishPath -Filter "*.pdb" -File -Recurse |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$requiredPublishFiles = @(
    "Cutwork.exe",
    "Cutwork.dll",
    "Cutwork.deps.json",
    "Cutwork.runtimeconfig.json",
    "appsettings.json",
    "Flamoris.Logging.dll",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "PresentationFramework.dll"
)

foreach ($requiredFile in $requiredPublishFiles) {
    $requiredPath = Join-Path $publishPath $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required publish output is missing: $requiredFile"
    }
}

$wpfNativeFiles = @(Get-ChildItem -LiteralPath $publishPath -File | Where-Object {
    $_.Name -match '^(PresentationNative|wpfgfx).*\.dll$'
})
if ($wpfNativeFiles.Count -lt 2) {
    throw "Expected self-contained WPF native runtime files were not published."
}

$productLaunchers = @(Get-ChildItem -LiteralPath $publishPath -Filter "*.exe" -File -Recurse | Where-Object {
    $_.Name -match '(?i)(cutwork|flamoris)'
})
if ($productLaunchers.Count -ne 2 -or @($productLaunchers | Where-Object Name -eq "Cutwork.exe").Count -ne 1 -or @($productLaunchers | Where-Object Name -eq "Cutwork.Bridge.exe").Count -ne 1) {
    $names = ($productLaunchers | ForEach-Object Name) -join ", "
    throw "Expected editor and bridge launchers; found: $names"
}

$forbiddenPublishFiles = @(Get-ChildItem -LiteralPath $publishPath -File -Recurse | Where-Object {
    $_.Extension -in @(".cs", ".csproj", ".sln", ".py", ".pdb") -or
    $_.Name -match '(?i)(opencv|python|tkinter)'
})
if ($forbiddenPublishFiles.Count -ne 0) {
    $names = ($forbiddenPublishFiles | ForEach-Object FullName) -join "`n"
    throw "Forbidden source, debug, experiment, or unused dependency content was published:`n$names"
}

$unsupportedCultureDirectories = @(Get-ChildItem -LiteralPath $publishPath -Directory | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)?$' -and $_.Name -notin @("ja", "en")
})
if ($unsupportedCultureDirectories.Count -ne 0) {
    $names = ($unsupportedCultureDirectories | ForEach-Object Name) -join ", "
    throw "Unsupported satellite resource cultures were published: $names"
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publishPath "*") -Destination $packageRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $packageRoot "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\README-ja.txt") -Destination (Join-Path $packageRoot "README-ja.txt")

$dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnetExecutable
$dotnetLicense = Join-Path $dotnetRoot "LICENSE.txt"
$dotnetNotices = Join-Path $dotnetRoot "ThirdPartyNotices.txt"
if (-not (Test-Path -LiteralPath $dotnetLicense -PathType Leaf)) {
    throw ".NET SDK license was not found beside dotnet: $dotnetLicense"
}
if (-not (Test-Path -LiteralPath $dotnetNotices -PathType Leaf)) {
    throw ".NET SDK third-party notices were not found beside dotnet: $dotnetNotices"
}
Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $packageRoot "DOTNET-LICENSE.txt")
Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $packageRoot "THIRD-PARTY-NOTICES.txt")

$packageFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName)
$inventory = @($packageFiles | ForEach-Object {
    [ordered]@{
        path = [System.IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$inventory | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $inventoryPath -Encoding utf8

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    foreach ($requiredEntry in @("Cutwork.exe", "LICENSE.txt", "DOTNET-LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "README-ja.txt")) {
        if (@($entryNames | Where-Object { $_ -ceq $requiredEntry }).Count -ne 1) {
            throw "ZIP must contain exactly one root entry named $requiredEntry."
        }
    }

    $zipProductLaunchers = @($entryNames | Where-Object {
        $_ -match '(?i)[^/]*(cutwork|flamoris)[^/]*\.exe$'
    })
    if ($zipProductLaunchers.Count -ne 2 -or "Cutwork.exe" -cnotin $zipProductLaunchers -or "mcp/Cutwork.Bridge.exe" -cnotin $zipProductLaunchers) {
        throw "ZIP must expose Cutwork.exe and mcp/Cutwork.Bridge.exe only."
    }

    $forbiddenEntries = @($entryNames | Where-Object {
        $_ -match '(^|/)(experiments|tests|src|obj|bin)(/|$)' -or
        $_ -match '(?i)\.(cs|csproj|sln|py|pdb)$' -or
        $_ -match '(?i)(opencv|python|tkinter)'
    })
    if ($forbiddenEntries.Count -ne 0) {
        throw "ZIP contains forbidden content: $($forbiddenEntries -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Portable package: $zipPath"
Write-Host "Inventory: $inventoryPath"
