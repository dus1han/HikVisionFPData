<#
.SYNOPSIS
  Stops and removes the HikSync Windows service.
#>
param([string] $ServiceName = "HikSync")

$ErrorActionPreference = "Stop"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Write-Host "Service '$ServiceName' is not installed."; return }

if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
sc.exe delete $ServiceName | Out-Null
Write-Host "Removed service '$ServiceName'."
