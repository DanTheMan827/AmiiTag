<#
.SYNOPSIS
    Publishes the Windows app: unpackaged and self-contained (see Properties/PublishProfiles/win-*.pubxml), zipped.

.EXAMPLE
    ./scripts/build-windows.ps1
    ./scripts/build-windows.ps1 -Configuration Debug -Runtime win-arm64
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [switch] $InstallWorkloads
)

. "$PSScriptRoot/common.ps1"
if (-not $IsWindows) { throw 'The Windows app can only be built on Windows.' }
if ($InstallWorkloads) { Install-MauiWorkloads }

$publish = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/windows-$Runtime-$Configuration")
Invoke-Dotnet publish $MauiProject -f net10.0-windows10.0.19041.0 -c $Configuration "-p:PublishProfile=$Runtime" "-p:PublishDir=$publish/"

$out = Join-Path $ArtifactsRoot 'windows'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Compress-Folder $publish (Join-Path $out "AmiiTag-windows-$Runtime-$Configuration.zip")
