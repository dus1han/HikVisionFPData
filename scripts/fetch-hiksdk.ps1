<#
.SYNOPSIS
  Downloads/extracts the Hikvision Device Network SDK (Win64) and places the runtime DLL set
  into src\HikSync.Service\native\.

.DESCRIPTION
  Hikvision does not publish HCNetSDK to any package manager, so there is no fully hands-off pull.
  Get the direct .zip link once from the official page (right-click the download button -> Copy link):
    https://www.hikvision.com/en/support/download/sdk/   (Device Network SDK for Windows 64-bit)
  then pass it as -ZipUrl. Or download the zip yourself and pass -ZipPath.

  The script finds the folder containing HCNetSDK.dll inside the archive and copies that folder's
  contents (including the HCNetSDKCom\ plugin folder and OpenSSL DLLs) into the native\ output.

.EXAMPLE
  .\fetch-hiksdk.ps1 -ZipUrl "https://download.hikvision.com/.../Device_Network_SDK_Win64.zip"
  .\fetch-hiksdk.ps1 -ZipPath "C:\Downloads\Device_Network_SDK_Win64.zip"
#>
[CmdletBinding(DefaultParameterSetName = 'Url')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Url')]  [string] $ZipUrl,
    [Parameter(Mandatory = $true, ParameterSetName = 'Path')] [string] $ZipPath,
    [string] $Dest
)

$ErrorActionPreference = "Stop"

# Resolve destination = <repo>\src\HikSync.Service\native
if (-not $Dest) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $Dest = Join-Path $repoRoot "src\HikSync.Service\native"
}
New-Item -ItemType Directory -Force $Dest | Out-Null

$work = Join-Path $env:TEMP ("hiksdk_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    if ($PSCmdlet.ParameterSetName -eq 'Url') {
        $ZipPath = Join-Path $work "sdk.zip"
        Write-Host "Downloading SDK zip (this is large, ~300+ MB)..."
        Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing
    }
    if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }

    Write-Host "Extracting..."
    $extract = Join-Path $work "x"
    Expand-Archive -Path $ZipPath -DestinationPath $extract -Force

    # Locate the folder that actually contains HCNetSDK.dll (nested layout varies by release).
    $core = Get-ChildItem $extract -Recurse -Filter "HCNetSDK.dll" -File | Select-Object -First 1
    if (-not $core) { throw "HCNetSDK.dll not found in the archive — is this the Win64 Device Network SDK?" }

    $libDir = $core.Directory.FullName
    Write-Host "Found runtime libraries in: $libDir"
    Copy-Item -Path (Join-Path $libDir '*') -Destination $Dest -Recurse -Force

    # Sanity check for the plugin folder.
    if (-not (Test-Path (Join-Path $Dest "HCNetSDKCom"))) {
        Write-Warning "HCNetSDKCom\ plugin folder was not copied — logins/config calls may fail. Check the archive layout."
    }

    # Surface the C# wrapper location — drop CHCNetSDK.cs into HikSync.Device to replace the hand-rolled interop.
    $wrapper = Get-ChildItem $extract -Recurse -Filter "CHCNetSDK.cs" -File | Select-Object -First 1
    if ($wrapper) { Write-Host "C# wrapper available: $($wrapper.FullName)" -ForegroundColor Green }

    Write-Host "Placed DLL set into: $Dest" -ForegroundColor Green
    Get-ChildItem $Dest | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
