<#
.SYNOPSIS
    Builds the ExtendSpace Android release APK and copies it, named by version, with its SHA-256, to
    artifacts/release (D72).

.DESCRIPTION
    Signed when apps/android/keystore.properties exists (see docs/release/release-process.md), unsigned
    otherwise; the name says which. Checks that the APK contains none of the debug-only screens
    (experiments and the session harness) before copying it. Never overwrites an earlier copy.
#>
[CmdletBinding()]
param([string] $OutputRoot)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$android = Join-Path $repo 'apps\android'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\release' }

Push-Location $android
try {
    & .\gradlew.bat assembleRelease --console=plain -q
    if ($LASTEXITCODE -ne 0) { throw "Gradle failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$outputs = Join-Path $android 'app\build\outputs\apk\release'
$signed = Test-Path (Join-Path $outputs 'app-release.apk')
$apk = Join-Path $outputs ($(if ($signed) { 'app-release.apk' } else { 'app-release-unsigned.apk' }))
$buildTools = Get-ChildItem (Join-Path $env:ANDROID_HOME 'build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$badging = & (Join-Path $buildTools.FullName 'aapt2.exe') dump badging $apk
$version = [regex]::Match(($badging -join "`n"), "versionName='([^']+)'").Groups[1].Value

# The debug-only screens must not be in a release APK.
$scratch = Join-Path ([IO.Path]::GetTempPath()) "extendspace-apk-check-$PID"
New-Item -ItemType Directory -Force $scratch | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($apk)
    try {
        foreach ($entry in $zip.Entries | Where-Object Name -like 'classes*.dex') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $scratch $entry.Name), $true)
        }
    }
    finally { $zip.Dispose() }
    $classes = Get-ChildItem $scratch -Filter 'classes*.dex' | ForEach-Object { & (Join-Path $buildTools.FullName 'dexdump.exe') $_.FullName } | Select-String 'Class descriptor'
    $debugOnly = @($classes | Where-Object { $_ -match 'extendspace/(livestream|decodertest|sessiontest)/' })
    if ($debugOnly.Count -gt 0) { throw "The release APK contains $($debugOnly.Count) debug-only classes; refusing to stage it." }
}
finally { Remove-Item -Recurse -Force $scratch }

$name = "ExtendSpace-$version-android$(if ($signed) { '' } else { '-unsigned' }).apk"
$target = Join-Path $OutputRoot $name
if (Test-Path $target) { throw "$target already exists; this script never overwrites earlier builds." }
New-Item -ItemType Directory -Force $OutputRoot | Out-Null
Copy-Item $apk $target
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$target.sha256" -Value "$hash  $name" -Encoding ascii
Write-Host "APK: $target ($([math]::Round((Get-Item $target).Length / 1MB, 1)) MB, $(if ($signed) { 'signed' } else { 'UNSIGNED: cannot be installed until signed' }))"
Write-Host "SHA-256: $hash"
