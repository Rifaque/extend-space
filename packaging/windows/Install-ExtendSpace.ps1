<#
.SYNOPSIS
    Installs ExtendSpace for the current user (D72): no administrator rights, no registry changes,
    no services, nothing listening on the network.

.DESCRIPTION
    Copies the app folder beside this script to %LOCALAPPDATA%\Programs\ExtendSpace and adds Start
    menu shortcuts for ExtendSpace and for uninstalling it. Running it again upgrades in place. It
    refuses to replace a copy that is running.

    -InstallRoot and -ShortcutFolder exist so the script can be tested without touching the real
    profile.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\ExtendSpace'),
    [string] $ShortcutFolder = (Join-Path ([Environment]::GetFolderPath('Programs')) 'ExtendSpace'),
    [switch] $DesktopShortcut
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $source 'ExtendSpace.exe'))) {
    throw "No app folder beside this script ($source). Extract the whole ZIP first, then run Install.cmd from it."
}

$exe = Join-Path $InstallRoot 'ExtendSpace.exe'
$running = Get-Process -Name 'ExtendSpace' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) }
if ($running) { throw 'ExtendSpace is running. Close it, then run the installer again.' }

$version = if (Test-Path (Join-Path $PSScriptRoot 'VERSION.txt')) { (Get-Content (Join-Path $PSScriptRoot 'VERSION.txt') -TotalCount 1).Trim() } else { 'unknown' }
$previous = if (Test-Path (Join-Path $InstallRoot 'VERSION.txt')) { (Get-Content (Join-Path $InstallRoot 'VERSION.txt') -TotalCount 1).Trim() } else { $null }

# Upgrade: the old files are removed first, so nothing from an earlier version is left behind. Only
# this folder, which the installer created, is touched.
if (Test-Path $InstallRoot) {
    if (-not (Test-Path (Join-Path $InstallRoot 'ExtendSpace.exe'))) {
        throw "$InstallRoot exists but does not look like an ExtendSpace installation; not touching it."
    }
    Remove-Item -Recurse -Force $InstallRoot
}

New-Item -ItemType Directory -Force $InstallRoot | Out-Null
Copy-Item -Recurse -Path (Join-Path $source '*') -Destination $InstallRoot
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-ExtendSpace.ps1') $InstallRoot
Set-Content -Path (Join-Path $InstallRoot 'VERSION.txt') -Value $version -Encoding ascii
# Files from a downloaded ZIP carry the internet zone mark; the copies are this user's own.
Get-ChildItem -Recurse -File $InstallRoot | Unblock-File

New-Item -ItemType Directory -Force $ShortcutFolder | Out-Null
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string] $path, [string] $target, [string] $arguments, [string] $description) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.Arguments = $arguments
    $link.WorkingDirectory = $InstallRoot
    $link.Description = $description
    $link.IconLocation = "$exe,0"
    $link.Save()
}
New-Shortcut (Join-Path $ShortcutFolder 'ExtendSpace.lnk') $exe '' 'Use an Android device as a display for Windows'
New-Shortcut (Join-Path $ShortcutFolder 'Uninstall ExtendSpace.lnk') 'powershell.exe' "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallRoot 'Uninstall-ExtendSpace.ps1')`" -ShortcutFolder `"$ShortcutFolder`"" 'Remove ExtendSpace from this PC'
if ($DesktopShortcut) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ExtendSpace.lnk') $exe '' 'Use an Android device as a display for Windows'
}

if ($previous) { Write-Host "ExtendSpace $previous was upgraded to $version in $InstallRoot." }
else { Write-Host "ExtendSpace $version was installed in $InstallRoot." }
Write-Host 'Start it from the Start menu: ExtendSpace.'
