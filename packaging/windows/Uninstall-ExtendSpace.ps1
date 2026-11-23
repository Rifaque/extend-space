<#
.SYNOPSIS
    Removes ExtendSpace installed by Install-ExtendSpace.ps1: its folder and its shortcuts. Nothing
    else was installed, so nothing else is removed. adb and the Android app are not touched.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\ExtendSpace'),
    [string] $ShortcutFolder = (Join-Path ([Environment]::GetFolderPath('Programs')) 'ExtendSpace')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallRoot 'ExtendSpace.exe'
$running = Get-Process -Name 'ExtendSpace' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) }
if ($running) { throw 'ExtendSpace is running. Close it, then uninstall again.' }

if (Test-Path $InstallRoot) {
    if (-not (Test-Path $exe)) { throw "$InstallRoot does not look like an ExtendSpace installation; not touching it." }
    # The uninstaller may be running from inside the folder; PowerShell has read it already.
    Remove-Item -Recurse -Force $InstallRoot
}

if (Test-Path $ShortcutFolder) { Remove-Item -Recurse -Force $ShortcutFolder }
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ExtendSpace.lnk'
if (Test-Path $desktop) {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($desktop).TargetPath
    if ($target -ieq $exe) { Remove-Item -Force $desktop }
}

Write-Host 'ExtendSpace was removed.'
