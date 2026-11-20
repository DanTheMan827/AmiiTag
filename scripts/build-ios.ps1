<#
.SYNOPSIS
    Builds an unsigned iOS .ipa (for re-signing, or sideloading tools that sign it themselves).

.DESCRIPTION
    Built for devices (ios-arm64) without code signing, then packed as Payload/AmiiTag.app in an .ipa. It won't
    install as is: sign it with your own certificate and a provisioning profile that includes NFC Tag Reading
    (AltStore, Sideloadly and the like do this), or build signed from Visual Studio / Xcode.

.EXAMPLE
    ./scripts/build-ios.ps1
    ./scripts/build-ios.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [int] $BuildNumber = 0,
    [string] $Version,
    [switch] $InstallWorkloads
)

. "$PSScriptRoot/common.ps1"
if (-not $IsMacOS) { throw 'The iOS app can only be built on macOS (with Xcode).' }
if ($InstallWorkloads) { Install-MauiWorkloads }

$output = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/ios-$Configuration")
Invoke-Dotnet build $MauiProject -f net10.0-ios -c $Configuration -r ios-arm64 `
    '-p:EnableCodeSigning=false' '-p:ProvisioningType=manual' '-p:CodesignKey=' '-p:CodesignProvision=' '-p:BuildIpa=false' `
    @(Get-VersionArguments $BuildNumber $Version)

$app = Find-BuiltApp 'net10.0-ios' $Configuration

$payloadRoot = New-CleanDirectory (Join-Path $output 'ipa')
$payload = Join-Path $payloadRoot 'Payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& ditto $app.FullName (Join-Path $payload 'AmiiTag.app')
if ($LASTEXITCODE -ne 0) { throw 'Copying the app failed.' }

$out = Join-Path $ArtifactsRoot 'ios'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$ipa = Join-Path $out "AmiiTag-ios-$Configuration-unsigned.ipa"
if (Test-Path $ipa) { Remove-Item $ipa -Force }
Push-Location $payloadRoot
try {
    & zip -qry $ipa 'Payload'
    if ($LASTEXITCODE -ne 0) { throw 'Creating the .ipa failed.' }
}
finally {
    Pop-Location
}

Write-Host "IPA: $ipa" -ForegroundColor Green
