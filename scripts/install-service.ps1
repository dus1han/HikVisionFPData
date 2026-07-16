<#
.SYNOPSIS
  Installs HikSync as a Windows service with auto-restart recovery.
.EXAMPLE
  .\install-service.ps1 -BinPath "C:\HikSync\HikSync.Service.exe"
  .\install-service.ps1 -BinPath "C:\HikSync\HikSync.Service.exe" -Account "DOMAIN\svc_hiksync"
#>
param(
    [Parameter(Mandatory = $true)] [string] $BinPath,
    [string] $ServiceName = "HikSync",
    [string] $DisplayName = "HikSync Attendance & Device Sync",
    [string] $Account
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BinPath)) { throw "Binary not found: $BinPath" }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists; stopping and removing first."
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$createArgs = @("create", $ServiceName, "binPath=", "`"$BinPath`"", "start=", "auto", "DisplayName=", "`"$DisplayName`"")
if ($Account) { $createArgs += @("obj=", "`"$Account`"") }
sc.exe @createArgs | Out-Null

# Auto-restart on failure: restart after 5s, reset failure count daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe description $ServiceName "Collects Hikvision attendance to a local Postgres and syncs users/fingerprints IN->OUT." | Out-Null

Start-Service $ServiceName
Write-Host "Installed and started '$ServiceName'."
Get-Service $ServiceName | Format-Table -AutoSize
