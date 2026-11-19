<#
.SYNOPSIS
    Builds everything this computer can: the web app everywhere, Windows and Android on Windows, Android, Mac and iOS
    on macOS. Debug and Release, into artifacts/.

.EXAMPLE
    ./scripts/build-all.ps1
    ./scripts/build-all.ps1 -Configurations Release -InstallWorkloads
#>
param(
    [ValidateSet('Release', 'Debug')] [string[]] $Configurations = @('Release', 'Debug'),
    [string] $WebBasePath = '/',
    [switch] $InstallWorkloads
)

. "$PSScriptRoot/common.ps1"
if ($InstallWorkloads -and ($IsWindows -or $IsMacOS)) { Install-MauiWorkloads }

foreach ($configuration in $Configurations) {
    & "$PSScriptRoot/build-web.ps1" -Configuration $configuration -BasePath $WebBasePath

    if ($IsWindows) {
        foreach ($runtime in 'win-x64', 'win-arm64') {
            & "$PSScriptRoot/build-windows.ps1" -Configuration $configuration -Runtime $runtime
        }
    }

    if ($IsWindows -or $IsMacOS) {
        & "$PSScriptRoot/build-android.ps1" -Configuration $configuration
    }

    if ($IsMacOS) {
        & "$PSScriptRoot/build-mac.ps1" -Configuration $configuration
        & "$PSScriptRoot/build-ios.ps1" -Configuration $configuration
    }
}

Write-Host "Done: $ArtifactsRoot" -ForegroundColor Green
