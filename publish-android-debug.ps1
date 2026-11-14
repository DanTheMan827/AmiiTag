<#
.SYNOPSIS
    Builds a debug APK of AmiiTag (signed with the debug keystore, assemblies embedded) and optionally installs it.

.EXAMPLE
    .\publish-android-debug.ps1
    .\publish-android-debug.ps1 -Install          # also adb install -r on the connected device
#>
param(
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'AmiiTag.Maui\AmiiTag.Maui.csproj'
$publishDir = Join-Path $PSScriptRoot 'AmiiTag.Maui\bin\publish\android-debug'

dotnet publish $project -f net10.0-android -c Debug -p:PublishProfile=android-debug-apk
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$apk = Get-ChildItem $publishDir -Filter '*-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "No signed APK in $publishDir." }

$named = Join-Path $publishDir 'AmiiTag-debug.apk'
Copy-Item $apk.FullName $named -Force
Write-Host "APK: $named"

if ($Install) {
    adb install -r $named
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
