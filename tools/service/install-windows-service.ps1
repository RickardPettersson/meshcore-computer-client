#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs MeshCore Computer Client as a Windows service that starts with the computer,
    and opens the website ports in Windows Firewall for the local network.

.EXAMPLE
    .\install-windows-service.ps1 -AppFolder "C:\Program Files\MeshCore"
#>
param(
    # Folder with MeshCore.Host.exe and appsettings.json (a published build).
    [Parameter(Mandatory)] [string] $AppFolder,
    [string] $ServiceName = "MeshCoreComputerClient",
    [int[]] $WebPorts = @(5080, 5443)
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $AppFolder "MeshCore.Host.exe"
if (-not (Test-Path $exe)) { throw "MeshCore.Host.exe not found in $AppFolder. Publish first: dotnet publish src/MeshCore.Host -p:PublishProfile=win-x64" }

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "A service named $ServiceName already exists. Run uninstall-windows-service.ps1 first."
}

# The app writes its database, keys, users and logs to the "data" folder next to the exe.
# Running as LocalService keeps it unprivileged; give that account write access to the app folder's data folder.
$data = Join-Path $AppFolder "data"
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /grant "NT AUTHORITY\LocalService:(OI)(CI)M" | Out-Null

New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "MeshCore Computer Client" `
    -Description "MeshCore companion radio: REST API and website" -StartupType Automatic `
    -Credential (New-Object System.Management.Automation.PSCredential ("NT AUTHORITY\LocalService", (New-Object System.Security.SecureString))) | Out-Null

# Restart automatically if it ever stops unexpectedly.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

foreach ($port in $WebPorts) {
    New-NetFirewallRule -DisplayName "MeshCore Computer Client ($port)" -Direction Inbound -Protocol TCP -LocalPort $port `
        -Profile Private -Action Allow | Out-Null
}

Start-Service -Name $ServiceName
Write-Host "Installed and started. Open http://localhost:5080 (or https://<this PC>:5443 from your phone)."
Write-Host "Logs: $data\logs"
