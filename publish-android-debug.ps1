<#
.SYNOPSIS
    Builds a debug APK of AmiiTag (debug keystore, assemblies embedded) and optionally installs it.
    Kept for convenience; scripts/build-android.ps1 does the work.

.EXAMPLE
    .\publish-android-debug.ps1
    .\publish-android-debug.ps1 -Install          # also adb install -r on the connected device
#>
param(
    [switch] $Install
)

& (Join-Path $PSScriptRoot 'scripts/build-android.ps1') -Configuration Debug -Install:$Install
