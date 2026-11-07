<#
.SYNOPSIS
    Builds the Windows release: a self-contained, single-file MeshCore.Host.exe (no .NET install needed),
    plus meshcore-cli.exe, the service scripts and the docs, zipped with a SHA-256 checksum.
    Used by .github/workflows/release.yml; run it locally to get the same result.

.EXAMPLE
    ./build/package-windows.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDir = "artifacts/release",
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Version numbers must look like 1.2.3 or 1.2.3-beta.1 for .NET.
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' isn't like 1.2.3 or 1.2.3-beta.1" }
$name = "MeshCoreComputerClient-$Version-win-x64"
$stage = Join-Path $root "artifacts/stage/$name"

function Invoke-Step([string] $title, [scriptblock] $block) {
    Write-Host "==> $title"
    & $block
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Write-Host "==> Test"
    & (Join-Path $PSScriptRoot "test.ps1")
}

Invoke-Step "Publish MeshCore.Host (win-x64, single file, self-contained)" {
    dotnet publish src/MeshCore.Host -p:PublishProfile=win-x64 -p:Version=$Version --nologo -v q
}
Invoke-Step "Publish meshcore-cli (win-x64, single file, self-contained)" {
    dotnet publish tools/MeshCore.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -p:Version=$Version -o artifacts/publish/cli-win-x64 --nologo -v q
}

Write-Host "==> Stage $name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$app = Join-Path $root "artifacts/publish/win-x64"
foreach ($exe in "$app/MeshCore.Host.exe", "artifacts/publish/cli-win-x64/meshcore-cli.exe") {
    if (-not (Test-Path $exe)) { throw "Publish produced no $exe (missing publish profile?)" }
}
Copy-Item "$app/*" $stage -Recurse -Exclude "data", "appsettings.Development.json", "web.config"
Copy-Item "artifacts/publish/cli-win-x64/meshcore-cli.exe" $stage
New-Item -ItemType Directory -Force -Path "$stage/service" | Out-Null
Copy-Item "tools/service/*-windows-service.ps1" "$stage/service"
New-Item -ItemType Directory -Force -Path "$stage/docs" | Out-Null
Copy-Item "docs/SETUP.md", "docs/HELTEC-V4-WIFI.md" "$stage/docs"
Copy-Item "README.md", "LICENSE" $stage

# A release must never ship someone's data, keys or users.
$leaks = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '\.(db|pfx|key)$' -or $_.Name -eq 'appsettings.auth.json' }
if ($leaks) { throw "Refusing to package private files: $($leaks.FullName -join ', ')" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zip = Join-Path $OutputDir "$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage/*" -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline

Write-Host "==> $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "    SHA-256 $hash"
if ($env:GITHUB_OUTPUT) {
    "zip=$zip" >> $env:GITHUB_OUTPUT
    "name=$name" >> $env:GITHUB_OUTPUT
}
