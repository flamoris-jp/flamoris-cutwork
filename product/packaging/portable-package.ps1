# Build-time helpers, copied into each independent desktop repository.
# This file is never distributed or loaded by the application.
function Assert-PortableContent {
    param([Parameter(Mandatory)][string]$PackageRoot, [string[]]$AllowedSourcePaths = @())
    foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($PackageRoot, $item.FullName).Replace('\', '/')
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Linked content is not portable: $relative"
        }
        if ($relative -match '(?i)(^|/)(src|test|tests|staging|history|experiments|obj|bin|\.git|\.github|node_modules|__pycache__)(/|$)') {
            # FFmpeg's bin directory is an intentional runtime location.
            if ($relative -notmatch '^ffmpeg/bin(/|$)') { throw "Development content packaged: $relative" }
        }
        if ($item.PSIsContainer) { continue }
        if ($relative -match '(?i)\.(cs|csproj|sln|slnx|cpp|cc|c|h|hpp|py|pyc|pdb|mjs|cjs|js|asar|ps1|sh|lib|obj|vcxproj)$' -and
            $relative -cnotin $AllowedSourcePaths) {
            throw "Source or debug content packaged: $relative"
        }
        if ($item.Name -match '(?i)^(node|electron)\.exe$|ProductHost|WindowsSmoke|testhost|(?:^|[-_.])tests?(?=[-_.]|$).*\.(exe|dll)$|^Microsoft\.TestPlatform') {
            throw "Obsolete or test runtime packaged: $relative"
        }
    }
}

function Assert-PortableArchive {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][object[]]$Inventory)
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $Inventory) { $expected.Add($file.path, $file) }
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $expected.ContainsKey($entry.FullName)) { throw "Unexpected or duplicate ZIP entry: $($entry.FullName)" }
            $file = $expected[$entry.FullName]
            if ($entry.Length -ne $file.size) { throw "ZIP size mismatch: $($entry.FullName)" }
            $stream = $entry.Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($hasher.ComputeHash($stream)).ToLowerInvariant() }
            finally { $stream.Dispose(); $hasher.Dispose() }
            if ($hash -cne $file.sha256) { throw "ZIP hash mismatch: $($entry.FullName)" }
            $null = $expected.Remove($entry.FullName)
        }
        if ($expected.Count -ne 0) { throw "ZIP is missing distributed files: $($expected.Keys -join ', ')" }
    } finally { $archive.Dispose() }
}

function Complete-PortablePackage {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string[]]$AllowedSourcePaths = @()
    )
    Assert-PortableContent -PackageRoot $PackageRoot -AllowedSourcePaths $AllowedSourcePaths
    $revision = & git -C $RepositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to record package source revision.' }
    $buildInfo = Join-Path $PackageRoot 'BUILD-INFO.txt'
    if (-not (Test-Path -LiteralPath $buildInfo)) {
        @("commit=$revision", 'platform=win-x64') | Set-Content -LiteralPath $buildInfo -Encoding utf8
    }
    $checksumPath = Join-Path $PackageRoot 'SHA256SUMS.txt'
    if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }
    $files = @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse -Force | Sort-Object FullName)
    $inventory = @($files | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    @($inventory | ForEach-Object { '{0}  {1}' -f $_.sha256, $_.path }) |
        Set-Content -LiteralPath $checksumPath -Encoding utf8
    # Checksums exclude themselves; the external inventory covers every ZIP file.
    $inventory += [ordered]@{
        path = 'SHA256SUMS.txt'
        size = (Get-Item -LiteralPath $checksumPath).Length
        sha256 = (Get-FileHash -LiteralPath $checksumPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $inventoryPath = "$PackageRoot.inventory.json"
    $zipPath = "$PackageRoot.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $inventory | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($PackageRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
        Assert-PortableArchive -ZipPath $zipPath -Inventory $inventory
    } catch {
        Remove-Item -LiteralPath $zipPath, $inventoryPath -Force -ErrorAction SilentlyContinue
        throw
    }
    Write-Host "Portable package: $zipPath"
    Write-Host "Inventory: $inventoryPath"
}
