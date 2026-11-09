<#
.SYNOPSIS
    Builds a release package for one platform: a self-contained, single-file MeshCore.Host (no .NET install needed),
    plus meshcore-cli, the service scripts and the docs, with a SHA-256 checksum.
    Windows gets a .zip; Linux and macOS get a .tar.gz (it keeps the executable bit).
    Used by .github/workflows/release.yml; run it locally to get the same result.

    When this computer can run the build (same OS and CPU, or an Intel Mac build on Apple silicon with Rosetta),
    it also runs a smoke test: "MeshCore.Host --self-check" (SQLite and SkiaSharp load), then starts the app
    with the simulator and waits for /healthz.

.EXAMPLE
    ./build/package.ps1 -Version 1.0.0                       # Windows x64
    ./build/package.ps1 -Version 1.0.0 -Runtime linux-arm64  # Raspberry Pi (64-bit OS)
    ./build/package.ps1 -Version 1.0.0 -Runtime osx-arm64    # Mac with Apple silicon
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")] [string] $Runtime = "win-x64",
    [string] $OutputDir = "artifacts/release",
    [switch] $SkipTests,
    [switch] $SkipSmokeTest,
    # Linux builds on a Debian/Ubuntu machine also get a .deb. This installs it on THIS machine (sudo apt), checks the
    # service runs, then removes and purges it again. Meant for the throwaway release runners.
    [switch] $TestDebInstall
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Version numbers must look like 1.2.3 or 1.2.3-beta.1 for .NET.
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' isn't like 1.2.3 or 1.2.3-beta.1" }
$name = "MeshCoreComputerClient-$Version-$Runtime"
$stage = Join-Path $root "artifacts/stage/$name"
$isWindowsBuild = $Runtime.StartsWith("win-")
$isMacBuild = $Runtime.StartsWith("osx-")
$exe = if ($isWindowsBuild) { ".exe" } else { "" }

function Invoke-Step([string] $title, [scriptblock] $block) {
    Write-Host "==> $title"
    & $block
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

# The platform this script runs on, as a .NET runtime identifier (works in Windows PowerShell 5.1 and pwsh 7).
function Get-HostRuntime {
    $arch = "$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)".ToLowerInvariant()
    if ($env:OS -eq "Windows_NT") { return "win-$arch" }
    $os = if ((uname -s) -eq "Darwin") { "osx" } else { "linux" }
    return "$os-$arch"
}

if (-not $SkipTests) {
    Write-Host "==> Test"
    & (Join-Path $PSScriptRoot "test.ps1")
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
}

Invoke-Step "Publish MeshCore.Host ($Runtime, single file, self-contained)" {
    dotnet publish src/MeshCore.Host -p:PublishProfile=$Runtime -p:Version=$Version --nologo -v q
}
Invoke-Step "Publish meshcore-cli ($Runtime, single file, self-contained)" {
    dotnet publish tools/MeshCore.Cli -c Release -r $Runtime --self-contained -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -p:Version=$Version -o "artifacts/publish/cli-$Runtime" --nologo -v q
}

Write-Host "==> Stage $name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$app = Join-Path $root "artifacts/publish/$Runtime"
$hostExe = "$app/MeshCore.Host$exe"
$cliExe = "artifacts/publish/cli-$Runtime/meshcore-cli$exe"
foreach ($file in $hostExe, $cliExe) {
    if (-not (Test-Path $file)) { throw "Publish produced no $file (missing publish profile?)" }
}
Copy-Item "$app/*" $stage -Recurse -Exclude "data", "appsettings.Development.json", "web.config"
Copy-Item $cliExe $stage
New-Item -ItemType Directory -Force -Path "$stage/service" | Out-Null
if ($isWindowsBuild) {
    Copy-Item "tools/service/*-windows-service.ps1" "$stage/service"
} elseif ($isMacBuild) {
    Copy-Item "tools/service/install-macos-service.sh" "$stage/service"
} else {
    Copy-Item "tools/service/install-linux-service.sh", "tools/service/meshcore.service" "$stage/service"
}
New-Item -ItemType Directory -Force -Path "$stage/docs" | Out-Null
Copy-Item "docs/SETUP.md", "docs/install-*.md", "docs/HELTEC-V4-WIFI.md" "$stage/docs"
Copy-Item "README.md", "LICENSE" $stage

# A release must never ship someone's data, keys or users.
$leaks = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '\.(db|pfx|key)$' -or $_.Name -eq 'appsettings.auth.json' }
if ($leaks) { throw "Refusing to package private files: $($leaks.FullName -join ', ')" }

# Apple silicon refuses to run unsigned code. The SDK signs macOS builds ad hoc when it runs on a Mac; check that, and
# sign them ad hoc if it didn't. (A build made on Windows or Linux can't be checked here: build Mac packages on a Mac.)
if ($isMacBuild -and (Get-HostRuntime).StartsWith("osx-")) {
    foreach ($file in "$stage/MeshCore.Host", "$stage/meshcore-cli") {
        codesign --verify $file 2>$null
        if ($LASTEXITCODE -ne 0) {
            Invoke-Step "Sign $(Split-Path $file -Leaf) (ad hoc)" { codesign --force --sign - $file }
        }
    }
}

# ---- Smoke test: does the packaged app run on this platform? ------------------------------------------------------
$hostRuntime = Get-HostRuntime
$launcher = @()
$canRun = $hostRuntime -eq $Runtime
if (-not $canRun -and $hostRuntime -eq "osx-arm64" -and $Runtime -eq "osx-x64") {
    /usr/bin/arch -x86_64 /usr/bin/true 2>$null
    if ($LASTEXITCODE -eq 0) { $canRun = $true; $launcher = @("/usr/bin/arch", "-x86_64") } # Rosetta 2
}
if ($SkipSmokeTest) {
    Write-Host "==> Smoke test skipped (-SkipSmokeTest)"
} elseif (-not $canRun) {
    Write-Host "==> Smoke test skipped: a $Runtime build can't run on this $hostRuntime computer"
} else {
    $binary = Join-Path $stage "MeshCore.Host$exe"
    if (-not $isWindowsBuild) { chmod +x $binary (Join-Path $stage "meshcore-cli") }

    Invoke-Step "Smoke test: MeshCore.Host --self-check" {
        if ($launcher) { & $launcher[0] $launcher[1] $binary --self-check | Out-Host } else { & $binary --self-check | Out-Host }
    }

    Write-Host "==> Smoke test: start with the simulator and wait for /healthz"
    $data = Join-Path ([System.IO.Path]::GetTempPath()) "meshcore-smoke-$([guid]::NewGuid().ToString('n'))"
    $appArgs = @("--MeshCore:Connection=simulator", "--MeshCore:DataDirectory=$data", "--MeshCore:ConsoleChat=false",
        "--Kestrel:Endpoints:Web:Url=http://127.0.0.1:5980", "--Kestrel:Endpoints:Api:Url=http://127.0.0.1:5981",
        "--Web:Https:Port=5943", "--Web:Https:RedirectHttp=false", "--BatteryLog:Enabled=false")
    $file = if ($launcher) { $launcher[0] } else { $binary }
    $fileArgs = if ($launcher) { @($launcher[1], $binary) + $appArgs } else { $appArgs }
    $log = Join-Path ([System.IO.Path]::GetTempPath()) "meshcore-smoke-$Runtime.log"
    $process = Start-Process -FilePath $file -ArgumentList $fileArgs -WorkingDirectory $stage -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    try {
        $healthy = $false
        $health = $null
        for ($i = 0; $i -lt 60 -and -not $process.HasExited; $i++) {
            Start-Sleep -Seconds 1
            try {
                $health = Invoke-RestMethod "http://127.0.0.1:5981/healthz" -TimeoutSec 5 # API port
                if ("$($health.state)" -eq "Connected") { $healthy = $true; break }
            } catch { }
        }
        if (-not $healthy) {
            Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 40 | ForEach-Object { Write-Host "    $_" }
            throw "Smoke test failed: the app didn't report a connected (simulated) radio within 60 s (last /healthz: $($health | ConvertTo-Json -Compress))"
        }
        $page = Invoke-WebRequest "http://127.0.0.1:5980/" -UseBasicParsing -TimeoutSec 10
        if ($page.StatusCode -ne 200) { throw "Smoke test failed: the website answered $($page.StatusCode)" }
        $httpsMade = Test-Path (Join-Path $data "https")
        Write-Host "    Website and API are up, the radio (simulator) is connected; HTTPS certificate made: $httpsMade"
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.WaitForExit()
        Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# ---- .deb (Ubuntu, Debian, Raspberry Pi OS) ---------------------------------------------------------------------------
# Built where dpkg-deb exists (the Linux release runners). Layout: program in /opt, settings in /etc (a conffile, kept on
# upgrade), data in /var/lib, a systemd service, and meshcore-cli on the PATH.
function Write-Unix([string] $path, [string] $text) {
    [System.IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
}

function Test-RootFile([string] $path) { sudo test -f $path; return $LASTEXITCODE -eq 0 }

function Test-DebInstall([string] $deb, [string] $package) {
    $deb = (Resolve-Path $deb).Path # apt installs a local file only when given a path
    $unit = "$package.service"
    Invoke-Step "Install test: apt install $(Split-Path $deb -Leaf)" { sudo apt-get install -y --no-install-recommends $deb }
    # Use the simulator instead of the radio in the default settings, through a drop-in (as a user would with systemctl edit).
    Invoke-Step "Install test: switch to the simulator" {
        sudo mkdir -p "/etc/systemd/system/$unit.d"
        "[Service]`nEnvironment=MeshCore__Connection=simulator`nEnvironment=BatteryLog__Enabled=false`n" | sudo tee "/etc/systemd/system/$unit.d/test.conf" | Out-Null
        sudo systemctl daemon-reload
        sudo systemctl restart $unit
    }
    try {
        $healthy = $false
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Seconds 1
            try {
                if ("$((Invoke-RestMethod 'http://127.0.0.1:5081/healthz' -TimeoutSec 5).state)" -eq "Connected") { $healthy = $true; break }
            } catch { }
        }
        if (-not $healthy) {
            sudo systemctl status $unit --no-pager | Out-Host
            sudo journalctl -u $unit --no-pager -n 60 | Out-Host
            throw "Install test failed: the service didn't report a connected (simulated) radio within 60 s"
        }
        # Default settings: port 5080 redirects to HTTPS on 5443, with the certificate the app makes itself (so the service
        # could write it). curl, because pwsh's Invoke-WebRequest -MaximumRedirection 0 throws on some versions.
        $redirect = curl -s -o /dev/null -m 10 -w "%{http_code} %{redirect_url}" "http://127.0.0.1:5080/"
        if ($redirect -notmatch '^30[1278] https://') { throw "Install test failed: port 5080 answered '$redirect', not a redirect to HTTPS" }
        $page = curl -s -o /dev/null -m 10 -k -L -w "%{http_code}" "https://127.0.0.1:5443/"
        if ($page -ne "200") { throw "Install test failed: the HTTPS website answered '$page'" }
        if (-not (Test-RootFile "/var/lib/$package/meshcore.db")) { throw "Install test failed: no database in /var/lib/$package" }
        meshcore-cli --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Install test failed: meshcore-cli isn't on the PATH" }
        Write-Host "    Service running as $(systemctl show -p User --value $unit), HTTP redirects to HTTPS, website up, data in /var/lib/$package, meshcore-cli on the PATH"
    } finally {
        sudo rm -rf "/etc/systemd/system/$unit.d"
    }

    Invoke-Step "Install test: apt remove (keeps data)" { sudo apt-get remove -y $package }
    if ((systemctl is-active $unit) -eq "active") { throw "Install test failed: the service still runs after apt remove" }
    if (-not (Test-RootFile "/var/lib/$package/meshcore.db")) { throw "Install test failed: apt remove deleted the data" }
    Invoke-Step "Install test: apt purge (deletes data and settings)" { sudo apt-get purge -y $package }
    if (Test-Path "/var/lib/$package") { throw "Install test failed: data left after apt purge" }
    if (Test-Path "/etc/$package") { throw "Install test failed: settings left after apt purge" }
    Write-Host "    Remove kept the data, purge removed it"
}

$debArch = @{ "linux-x64" = "amd64"; "linux-arm64" = "arm64" }[$Runtime]
$deb = $null
if ($debArch -and (Get-Command dpkg-deb -ErrorAction SilentlyContinue)) {
    $package = "meshcore-computer-client"
    $debVersion = $Version -replace '-', '~' # 1.2.0-beta.1 → 1.2.0~beta.1, which Debian sorts before 1.2.0
    $tree = Join-Path $root "artifacts/stage/deb-$name"
    Write-Host "==> Stage ${package}_${debVersion}_$debArch.deb"
    Remove-Item $tree -Recurse -Force -ErrorAction SilentlyContinue
    $opt = "$tree/opt/$package"
    $etc = "$tree/etc/$package"
    $doc = "$tree/usr/share/doc/$package"
    foreach ($dir in $opt, $etc, $doc, "$tree/usr/bin", "$tree/usr/lib/systemd/system", "$tree/DEBIAN") { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    Copy-Item "$stage/*" $opt -Recurse -Exclude "service", "docs", "README.md", "LICENSE", "appsettings.json"
    Copy-Item "$stage/appsettings.json" "$etc/appsettings.json"
    ln -s "/etc/$package/appsettings.json" "$opt/appsettings.json"
    ln -s "/opt/$package/meshcore-cli" "$tree/usr/bin/meshcore-cli"
    Copy-Item "tools/deb/$package.service" "$tree/usr/lib/systemd/system/"
    Copy-Item "README.md", "docs/*.md" $doc
    Write-Unix "$doc/copyright" (@"
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: MeshCore Computer Client
Source: https://github.com/RickardPettersson/meshcore-computer-client

Files: *
License: MIT

"@ + (Get-Content LICENSE -Raw))

    foreach ($script in "postinst", "prerm", "postrm") { Copy-Item "tools/deb/$script" "$tree/DEBIAN/$script" }
    Write-Unix "$tree/DEBIAN/conffiles" "/etc/$package/appsettings.json`n"
    $sizeKb = [math]::Ceiling(((Get-ChildItem $tree -Recurse -File | Where-Object { $_.FullName -notlike "*DEBIAN*" } | Measure-Object Length -Sum).Sum) / 1KB)
    # Self-contained .NET needs only the C/C++ runtime, ICU (text handling) and OpenSSL (HTTPS) from the system.
    $icu = (78, 77, 76, 75, 74, 73, 72, 71, 70, 69, 68, 67, 66, 65, 63, 60 | ForEach-Object { "libicu$_" }) -join " | "
    Write-Unix "$tree/DEBIAN/control" @"
Package: $package
Version: $debVersion
Architecture: $debArch
Maintainer: Rickard Pettersson <RickardPettersson@users.noreply.github.com>
Installed-Size: $sizeKb
Depends: libc6, libgcc-s1 | libgcc1, libstdc++6, $icu, libssl3t64 | libssl3 | libssl1.1, ca-certificates
Section: hamradio
Priority: optional
Homepage: https://github.com/RickardPettersson/meshcore-computer-client
Description: Computer client for MeshCore companion radios
 Connects to a MeshCore companion radio over WiFi or USB, stores messages,
 contacts and channels, and serves a website for phones and desktops on your
 network plus a REST API. Runs as the meshcore-computer-client service.
 .
 Settings: /etc/meshcore-computer-client/appsettings.json
 Data: /var/lib/meshcore-computer-client

"@

    chmod -R u=rwX,go=rX $tree
    chmod 755 "$opt/MeshCore.Host" "$opt/meshcore-cli" "$tree/DEBIAN/postinst" "$tree/DEBIAN/prerm" "$tree/DEBIAN/postrm"
    $deb = Join-Path $OutputDir "${package}_${debVersion}_$debArch.deb"
    Remove-Item $deb -ErrorAction SilentlyContinue
    Invoke-Step "Build $(Split-Path $deb -Leaf)" { dpkg-deb --root-owner-group -Zxz --build $tree $deb | Out-Host }
    Invoke-Step "Check $(Split-Path $deb -Leaf)" { dpkg-deb --info $deb | Out-Host }

    if ($TestDebInstall) {
        if ($hostRuntime -ne $Runtime) { throw "-TestDebInstall needs a $Runtime machine (this is $hostRuntime)" }
        Test-DebInstall $deb $package
    }
} elseif ($debArch) {
    Write-Host "==> .deb skipped: no dpkg-deb here (it's built on the Linux release runners)"
}

# ---- Archive --------------------------------------------------------------------------------------------------------
if ($isWindowsBuild) {
    $archive = Join-Path $OutputDir "$name.zip"
    Remove-Item $archive -ErrorAction SilentlyContinue
    Compress-Archive -Path "$stage/*" -DestinationPath $archive -CompressionLevel Optimal
} else {
    # Everything under one folder, with MeshCore.Host, meshcore-cli and the scripts executable.
    $archive = Join-Path $OutputDir "$name.tar.gz"
    Remove-Item $archive -ErrorAction SilentlyContinue
    $executables = @("MeshCore.Host", "meshcore-cli") + @(Get-ChildItem "$stage/service" -Filter *.sh | ForEach-Object { "service/$($_.Name)" })
    Invoke-Step "Pack $name.tar.gz" { dotnet run (Join-Path $PSScriptRoot "pack-tar.cs") -- $stage $archive $name @executables }
}
$archiveName = Split-Path $archive -Leaf
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $archiveName" -Encoding ascii -NoNewline

Write-Host "==> $archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
Write-Host "    SHA-256 $hash"
if ($deb) {
    $debName = Split-Path $deb -Leaf
    $debHash = (Get-FileHash $deb -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$deb.sha256" -Value "$debHash  $debName" -Encoding ascii -NoNewline
    Write-Host "==> $deb ($([math]::Round((Get-Item $deb).Length / 1MB, 1)) MB)"
    Write-Host "    SHA-256 $debHash"
}
if ($env:GITHUB_OUTPUT) {
    "archive=$archive" >> $env:GITHUB_OUTPUT
    "name=$name" >> $env:GITHUB_OUTPUT
}
