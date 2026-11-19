<#
.SYNOPSIS
    Builds the Mac Catalyst app without a developer certificate, as a zipped .app.

.DESCRIPTION
    Release is universal (Apple Silicon and Intel). The app is ad-hoc signed (no identity, no entitlements), which
    Apple Silicon needs to run it at all; it isn't notarized, so the first launch needs right click > Open (or
    xattr -dr com.apple.quarantine AmiiTag.app).

.EXAMPLE
    ./scripts/build-mac.ps1
    ./scripts/build-mac.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [int] $BuildNumber = 0,
    [switch] $InstallWorkloads
)

. "$PSScriptRoot/common.ps1"
if (-not $IsMacOS) { throw 'The Mac app can only be built on macOS.' }
if ($InstallWorkloads) { Install-MauiWorkloads }

$output = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/mac-$Configuration")
# %3B: a ";" inside -p:Name=value would end the property; MSBuild turns %3B back into ";".
$rids = if ($Configuration -eq 'Release') { 'maccatalyst-x64%3Bmaccatalyst-arm64' } else { "maccatalyst-$(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' })" }
Invoke-Dotnet build $MauiProject -f net10.0-maccatalyst -c $Configuration "-p:RuntimeIdentifiers=$rids" `
    '-p:EnableCodeSigning=false' '-p:CreatePackage=false' @(Get-VersionArguments $BuildNumber)

$app = Find-BuiltApp 'net10.0-maccatalyst' $Configuration
$staged = Join-Path $output 'AmiiTag.app'
& ditto $app.FullName $staged
if ($LASTEXITCODE -ne 0) { throw 'Copying the app failed.' }

& codesign --force --deep --sign - $staged
if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc signing failed.' }

$out = Join-Path $ArtifactsRoot 'mac'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Compress-Folder $staged (Join-Path $out "AmiiTag-mac-$Configuration.zip") -IncludeFolder
