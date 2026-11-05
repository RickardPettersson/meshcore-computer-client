#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the MeshCore Computer Client Windows service and its firewall rules.
    The app folder and its data (messages, users, keys) are left untouched.
#>
param([string] $ServiceName = "MeshCoreComputerClient")

$ErrorActionPreference = "Stop"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed."
}
Get-NetFirewallRule -DisplayName "MeshCore Computer Client (*)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Firewall rules removed. Your data folder was not touched."
