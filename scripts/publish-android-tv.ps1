[CmdletBinding()]
param(
    # Debug builds are faster to produce; Release is what you sideload onto the TV.
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Optional version overrides, e.g. -VersionName 2.0.1 -VersionCode 3
    [string]$VersionName,
    [string]$VersionCode,

    # Optional signing. Without these the APK is signed with the local debug key,
    # which is fine for sideloading but not for store distribution.
    [string]$KeystorePath,
    [string]$KeystorePassword,
    [string]$KeyAlias,
    [string]$KeyPassword,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/MioReel.Desktop/MioReel.Desktop.csproj'
$framework = 'net10.0-android36.0'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

# Do NOT pass -r android-arm here: restore evaluates both TFMs and the desktop
# TFM would try to resolve a CoreCLR linux-bionic-arm runtime pack. The Android
# TFM owns RuntimeIdentifiers=android-arm in the .csproj instead.
$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-f', $framework,
    '-p:AndroidPackageFormat=apk'
)

if ($NoRestore) {
    $arguments += '--no-restore'
}

if ($VersionName) {
    $arguments += "-p:ApplicationDisplayVersion=$VersionName"
}

if ($VersionCode) {
    $arguments += "-p:ApplicationVersion=$VersionCode"
}

if ($KeystorePath) {
    $resolvedKeystore = (Resolve-Path -LiteralPath $KeystorePath).Path
    if (-not $KeystorePassword) { throw '-KeystorePassword is required when -KeystorePath is used.' }
    if (-not $KeyAlias) { throw '-KeyAlias is required when -KeystorePath is used.' }
    if (-not $KeyPassword) { $KeyPassword = $KeystorePassword }

    $arguments += @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$resolvedKeystore",
        "-p:AndroidSigningStorePass=$KeystorePassword",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningKeyPass=$KeyPassword"
    )
}

Write-Host "Publishing Android TV ($Configuration, armeabi-v7a)..." -ForegroundColor Cyan
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishDir = Join-Path $repoRoot "src/MioReel.Desktop/bin/$Configuration/$framework/publish"
$apk = Get-ChildItem -LiteralPath $publishDir -Filter '*Signed.apk' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $apk) {
    throw "APK not found under $publishDir"
}

Write-Host ''
Write-Host "APK: $($apk.FullName)" -ForegroundColor Green
Write-Host ("Size: {0:N1} MB" -f ($apk.Length / 1MB))

if (-not $KeystorePath) {
    Write-Host 'Signed with the local debug key (sideload/test only).' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Install on a connected TV / emulator with:'
Write-Host "  adb install -r `"$($apk.FullName)`""