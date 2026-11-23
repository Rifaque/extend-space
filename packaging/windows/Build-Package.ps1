<#
.SYNOPSIS
    Builds the ExtendSpace Windows beta package (D72): a self-contained, unpackaged build of the host
    in a ZIP with a per-user installer, and its SHA-256.

.DESCRIPTION
    Publishes src/ExtendSpace.Host for one runtime with the .NET runtime and the Windows App SDK
    runtime inside it, so a tester needs neither installed. Nothing is signed (no certificate is
    available; see docs/release/release-process.md). The output goes to artifacts/release, which is
    git-ignored; nothing in the source tree is changed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\windows\Build-Package.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\release' }

[xml] $props = Get-Content (Join-Path $repo 'apps\windows\Directory.Build.props')
$prefix = $props.Project.PropertyGroup.VersionPrefix
$suffix = $props.Project.PropertyGroup.VersionSuffix
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

$name = "ExtendSpace-$version-$Runtime"
$staging = Join-Path $OutputRoot $name
$zip = Join-Path $OutputRoot "$name.zip"
if (Test-Path $staging) { throw "$staging already exists. Move it away first; this script never deletes earlier builds." }
if (Test-Path $zip) { throw "$zip already exists. Move it away first; this script never overwrites earlier builds." }
New-Item -ItemType Directory -Force $OutputRoot | Out-Null

$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$started = (Get-Date).AddSeconds(-2)
$project = Join-Path $repo 'apps\windows\src\ExtendSpace.Host\ExtendSpace.Host.csproj'
Write-Host "Building $version for $Runtime from clean ..."
& dotnet build $project -c Release -r $Runtime -p:Platform=$platform --self-contained true --no-incremental -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
Write-Host 'Publishing ...'
& dotnet publish $project -c Release -r $Runtime -p:Platform=$platform --self-contained true --no-build -o (Join-Path $staging 'app') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
$appDir = Join-Path $staging 'app'
if (-not (Test-Path (Join-Path $appDir 'ExtendSpace.exe'))) { throw 'The publish output has no ExtendSpace.exe.' }

# An unpackaged WinUI publish leaves out the app's own resource index (ExtendSpace.pri) and its
# compiled XAML (*.xbf); without them the window cannot load and the app crashes at start with
# 0xC000027B (found by package validation, ES-110). They are copied from the build output this
# publish has just produced (bin\<platform>\Release), and only files written during this run, so
# that a stale build can never be packaged; the package is refused if any is missing.
$binRelease = Join-Path $repo "apps\windows\src\ExtendSpace.Host\bin\$platform\Release"
$buildOut = Get-ChildItem $binRelease -Directory | Where-Object Name -like 'net*-windows*' | ForEach-Object { Join-Path $_.FullName $Runtime } |
    Where-Object { (Test-Path (Join-Path $_ 'ExtendSpace.pri')) -and ((Get-Item (Join-Path $_ 'ExtendSpace.pri')).LastWriteTime -ge $started) } |
    Select-Object -First 1
if (-not $buildOut) { throw "This run's build output (ExtendSpace.pri written after $started) was not found under $binRelease for $Runtime." }
Get-ChildItem $buildOut -Recurse -File -Include '*.pri', '*.xbf' | Where-Object { $_.LastWriteTime -ge $started } | ForEach-Object {
    $destination = Join-Path $appDir $_.FullName.Substring($buildOut.Length + 1)
    if (-not (Test-Path $destination)) {
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        Copy-Item $_.FullName $destination
    }
}
foreach ($required in 'ExtendSpace.pri', 'App.xbf', 'MainWindow.xbf', 'Pages\HomePage.xbf') {
    if (-not (Test-Path (Join-Path $appDir $required))) { throw "The package would be missing $required; refusing to build it." }
}

foreach ($file in 'Install.cmd', 'Install-ExtendSpace.ps1', 'Uninstall-ExtendSpace.ps1') {
    Copy-Item (Join-Path $PSScriptRoot $file) $staging
}
Copy-Item (Join-Path $repo 'docs\beta\install-windows.md') (Join-Path $staging 'README.md')
# The guides README.md links to, so that its links work inside the extracted folder.
foreach ($guide in 'troubleshooting.md', 'known-issues.md', 'privacy.md', 'quick-start.md', 'install-android.md', 'reporting.md', 'test-checklist.md') {
    Copy-Item (Join-Path $repo "docs\beta\$guide") $staging
}
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'LICENSE.txt')
Set-Content -Path (Join-Path $staging 'VERSION.txt') -Value $version -Encoding ascii

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Package: $zip ($size MB)"
Write-Host "SHA-256: $hash"
