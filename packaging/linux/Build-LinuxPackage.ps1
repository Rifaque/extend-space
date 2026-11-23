<#
.SYNOPSIS
    Builds the ExtendSpace Linux package: a self-contained linux-x64 tarball with a per-user installer,
    copied with its SHA-256 to artifacts/release (D102). Never overwrites an earlier copy.
#>
[CmdletBinding()]
param([string] $OutputRoot)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\release' }
[xml] $props = Get-Content (Join-Path $repo 'apps\windows\Directory.Build.props')
$version = "$($props.Project.PropertyGroup.VersionPrefix)-$($props.Project.PropertyGroup.VersionSuffix)".TrimEnd('-')
$name = "ExtendSpace-$version-linux-x64"
$staging = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
$root = Join-Path $staging $name
New-Item -ItemType Directory -Force (Join-Path $root 'app') | Out-Null
try {
    & dotnet publish (Join-Path $repo 'apps\linux\src\ExtendSpace.Host.Linux') -c Release -r linux-x64 --self-contained -o (Join-Path $root 'app') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    foreach ($file in 'install.sh', 'uninstall.sh', 'extendspace.desktop') {
        # LF line endings: the scripts run under /bin/sh.
        $text = (Get-Content -Raw (Join-Path $PSScriptRoot $file)) -replace "`r`n", "`n"
        [IO.File]::WriteAllText((Join-Path $root $file), $text)
    }
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "$version`n")
    New-Item -ItemType Directory -Force $OutputRoot | Out-Null
    $archive = Join-Path $OutputRoot "$name.tar.gz"
    if (Test-Path $archive) { throw "$archive exists; remove it or bump the version." }
    # The system bsdtar: a GNU tar from Git for Windows reads "C:" as a remote host.
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -czf $archive -C $staging $name
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256", "$hash  $name.tar.gz`n")
    Write-Output "$archive"
    Write-Output "SHA-256 $hash"
}
finally { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue }
