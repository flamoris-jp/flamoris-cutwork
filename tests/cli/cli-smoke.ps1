$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Windows-only: use the platform drawing library solely for a deterministic test image.
Add-Type -AssemblyName System.Drawing
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $repositoryRoot "src/Cutwork.Cli/Cutwork.Cli.csproj"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("cutwork-cli-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $image = Join-Path $temp "art.png"
    $projectOne = Join-Path $temp "imported.flimg"
    $projectTwo = Join-Path $temp "part.flimg"
    $handoff = Join-Path $temp "handoff.zip"
    $composite = Join-Path $temp "composite.png"

    $bitmap = [System.Drawing.Bitmap]::new(64, 64)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([System.Drawing.Color]::Orange) }
        finally { $graphics.Dispose() }
        $bitmap.Save($image, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }

    & dotnet run --project $project -c Release --no-build -- import $image --out $projectOne | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "import failed" }

    & dotnet run --project $project -c Release --no-build -- part-add $projectOne --polygon "8,8;55,8;55,55;8,55" --name "Character" --semantic character --out $projectTwo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "part-add failed" }

    $report = & dotnet run --project $project -c Release --no-build -- inspect $projectTwo | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $report.width -ne 64 -or $report.height -ne 64 -or $report.layers.Count -ne 2) {
        throw "inspect failed to report Base and Part"
    }
    if ($report.layers[0].semanticName -ne "character") {
        throw "part semantics missing"
    }

    & dotnet run --project $project -c Release --no-build -- composite $projectTwo --out $composite | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $composite)) { throw "composite failed" }

    & dotnet run --project $project -c Release --no-build -- handoff $projectTwo --out $handoff | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $handoff)) { throw "handoff failed" }

    # Accidental overwrites must fail without changing the existing project.
    & dotnet run --project $project -c Release --no-build -- import $image --out $projectOne 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "overwrite was not rejected" }

    Write-Host "Cutwork CLI smoke: PASS"
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
