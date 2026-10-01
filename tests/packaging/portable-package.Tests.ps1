$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'product/packaging/portable-package.ps1')

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Fails([scriptblock]$Action, [string]$MessagePattern) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught -like $MessagePattern) "Expected failure '$MessagePattern'; got '$caught'"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('portable-contract-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $temp 'FLAMORIS-Fixture-win-x64'
New-Item -ItemType Directory -Path (Join-Path $package 'mcp') -Force | Out-Null
try {
    New-Item -ItemType Directory (Join-Path $package 'empty-runtime-directory') | Out-Null
    # Synthetic binaries test packaging, without requiring a WPF host or network.
    Set-Content (Join-Path $package 'Fixture.exe') 'synthetic editor'
    Set-Content (Join-Path $package 'mcp/Flamoris.Mcp.Bridge.exe') 'synthetic bridge'
    Set-Content (Join-Path $package '日本語 name.txt') 'unicode path and content'
    Complete-PortablePackage -PackageRoot $package -RepositoryRoot $repo
    $inventory = @(Get-Content "$package.inventory.json" -Raw | ConvertFrom-Json)
    Assert-True ($inventory.Count -eq 5) 'External inventory must cover all files including build info and checksums.'
    $checksums = @(Get-Content (Join-Path $package 'SHA256SUMS.txt'))
    Assert-True ($checksums.Count -eq 4) 'Checksum list must exclude itself.'
    Assert-True (-not ($checksums | Where-Object { $_ -match '  SHA256SUMS.txt$' })) 'Recursive checksum entry.'
    foreach ($file in $inventory) {
        $actual = Get-Item -LiteralPath (Join-Path $package $file.path)
        Assert-True ($actual.Length -eq $file.size) "Inventory size differs: $($file.path)"
        Assert-True ((Get-FileHash $actual.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $file.sha256) "Inventory hash differs: $($file.path)"
        if ($file.path -cne 'SHA256SUMS.txt') {
            Assert-True (($file.sha256 + '  ' + $file.path) -cin $checksums) 'Checksum line differs from inventory.'
        }
    }
    $before = Get-Content (Join-Path $package 'SHA256SUMS.txt') -Raw
    Complete-PortablePackage -PackageRoot $package -RepositoryRoot $repo
    Assert-True ((Get-Content (Join-Path $package 'SHA256SUMS.txt') -Raw) -ceq $before) 'Repeated assembly must not hash the old checksum file.'

    $source = Join-Path $package 'recipe-worker.py'
    Set-Content $source '# required runtime'
    Assert-Fails { Assert-PortableContent $package } '*Source or debug*'
    Assert-PortableContent -PackageRoot $package -AllowedSourcePaths @('recipe-worker.py')
    Set-Content (Join-Path $package 'mcp/recipe-worker.py') '# accidental source'
    Assert-Fails { Assert-PortableContent -PackageRoot $package -AllowedSourcePaths @('recipe-worker.py') } '*Source or debug*'
    Remove-Item $source, (Join-Path $package 'mcp/recipe-worker.py')
    foreach ($name in @('node.exe', 'Fixture.Tests.dll', 'native_tests.exe', 'thing.pdb', 'thing.cpp')) {
        $bad = Join-Path $package $name
        Set-Content $bad 'forbidden'
        Assert-Fails { Assert-PortableContent $package } '*packaged*'
        Remove-Item $bad
    }
    $metadata = Join-Path $package '.git'
    New-Item -ItemType Directory $metadata | Out-Null
    Set-Content (Join-Path $metadata 'config') 'metadata'
    Assert-Fails { Assert-PortableContent $package } '*Development content*'
    Remove-Item $metadata -Recurse -Force
    New-Item -ItemType Directory (Join-Path $package 'ffmpeg/bin') -Force | Out-Null
    Set-Content (Join-Path $package 'ffmpeg/bin/ffmpeg.exe') 'encoder'
    Assert-PortableContent $package
    Remove-Item (Join-Path $package 'ffmpeg') -Recurse -Force

    # A same-sized modified ZIP entry must fail even when names/sizes match.
    $modified = Join-Path $temp 'modified'
    [IO.Compression.ZipFile]::ExtractToDirectory("$package.zip", $modified)
    Set-Content (Join-Path $modified 'Fixture.exe') 'Synthetic editor'
    $badZip = Join-Path $temp 'bad.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($modified, $badZip)
    Assert-Fails { Assert-PortableArchive $badZip $inventory } '*ZIP hash mismatch*'
    Remove-Item $badZip
    Remove-Item (Join-Path $modified 'Fixture.exe')
    [IO.Compression.ZipFile]::CreateFromDirectory($modified, $badZip)
    Assert-Fails { Assert-PortableArchive $badZip $inventory } '*ZIP is missing*'
    Remove-Item $badZip
    Set-Content (Join-Path $modified 'extra.txt') 'unexpected'
    [IO.Compression.ZipFile]::CreateFromDirectory($modified, $badZip)
    Assert-Fails { Assert-PortableArchive $badZip $inventory } '*Unexpected or duplicate*'
    $duplicateZip = Join-Path $temp 'duplicate.zip'
    Copy-Item "$package.zip" $duplicateZip
    $archive = [IO.Compression.ZipFile]::Open($duplicateZip, [IO.Compression.ZipArchiveMode]::Update)
    try { $null = $archive.CreateEntry('Fixture.exe') } finally { $archive.Dispose() }
    Assert-Fails { Assert-PortableArchive $duplicateZip $inventory } '*Unexpected or duplicate*'
    Write-Host 'PASS: portable integrity, Unicode paths, repeat assembly, source exceptions, hygiene and ZIP corruption/missing/extra entries'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
