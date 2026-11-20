<#
.SYNOPSIS
    Builds a sideloadable Android APK (not an AAB).

.DESCRIPTION
    Signed with a keystore when one is given (parameters, or ANDROID_KEYSTORE_BASE64 / ANDROID_KEYSTORE_PASSWORD /
    ANDROID_KEY_ALIAS / ANDROID_KEY_PASSWORD in the environment, as the CI workflow passes its secrets); otherwise with
    the Android debug keystore, which the SDK creates if it doesn't exist yet. Debug builds embed their assemblies so
    the APK runs on its own (see Properties/PublishProfiles/android-debug-apk.pubxml).

.EXAMPLE
    ./scripts/build-android.ps1
    ./scripts/build-android.ps1 -Configuration Debug -Install
    ./scripts/build-android.ps1 -KeystorePath my.keystore -KeyAlias amiitag     # passwords from the environment
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $KeystorePath,
    [string] $KeyAlias = $env:ANDROID_KEY_ALIAS,
    [int] $BuildNumber = 0,
    [string] $Version,
    [switch] $InstallWorkloads,
    [switch] $InstallAndroidDependencies,
    [switch] $Install
)

. "$PSScriptRoot/common.ps1"
if ($InstallWorkloads) { Install-MauiWorkloads }

if ($InstallAndroidDependencies) {
    # The Android SDK platform and build tools the workload targets (CI runners don't always have the newest).
    $sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $null }
    $sdkArgs = @()
    if ($sdk) { $sdkArgs += "-p:AndroidSdkDirectory=$sdk" }
    Invoke-Dotnet build $MauiProject '-t:InstallAndroidDependencies' -f net10.0-android '-p:AcceptAndroidSDKLicenses=True' @sdkArgs
}

$signing = @()
$temporaryKeystore = $null
if (-not $KeystorePath -and $env:ANDROID_KEYSTORE_BASE64) {
    $temporaryKeystore = Join-Path ([System.IO.Path]::GetTempPath()) "amiitag-$([guid]::NewGuid().ToString('N')).keystore"
    [System.IO.File]::WriteAllBytes($temporaryKeystore, [Convert]::FromBase64String($env:ANDROID_KEYSTORE_BASE64))
    $KeystorePath = $temporaryKeystore
}

if ($KeystorePath) {
    if (-not $KeyAlias) { throw 'A keystore needs its key alias (-KeyAlias or ANDROID_KEY_ALIAS).' }
    if (-not $env:ANDROID_KEYSTORE_PASSWORD) { throw 'Set ANDROID_KEYSTORE_PASSWORD (and ANDROID_KEY_PASSWORD if it differs).' }
    if (-not $env:ANDROID_KEY_PASSWORD) { $env:ANDROID_KEY_PASSWORD = $env:ANDROID_KEYSTORE_PASSWORD }

    # env: makes the build read the passwords from the environment, so they never appear in a command line or log.
    $signing = @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$((Resolve-Path $KeystorePath).Path)",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        '-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD',
        '-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD'
    )
    Write-Host "Signing with $KeystorePath ($KeyAlias)."
}
else {
    $signing = @('-p:AndroidKeyStore=false')
    Write-Host 'No keystore: signing with the Android debug keystore.'
}

try {
    $publish = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/android-$Configuration")
    $common = @('-f', 'net10.0-android', '-c', $Configuration, "-p:PublishDir=$publish/", '-p:AndroidPackageFormat=apk', '-p:AndroidPackageFormats=apk')
    if ($Configuration -eq 'Debug') { $common += '-p:PublishProfile=android-debug-apk' }
    Invoke-Dotnet publish $MauiProject @common @signing @(Get-VersionArguments $BuildNumber $Version)
}
finally {
    if ($temporaryKeystore) { Remove-Item $temporaryKeystore -Force -ErrorAction SilentlyContinue }
}

$apk = Get-ChildItem $publish -Filter '*-Signed.apk' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "No signed APK in $publish." }

$out = Join-Path $ArtifactsRoot 'android'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$named = Join-Path $out "AmiiTag-android-$Configuration.apk"
Copy-Item $apk.FullName $named -Force
Write-Host "APK: $named" -ForegroundColor Green

if ($Install) {
    # adb from PATH, the Android SDK the environment names, or the one Visual Studio / Android Studio installs.
    $adb = (Get-Command adb -ErrorAction SilentlyContinue)?.Source
    if (-not $adb) {
        $roots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "${env:ProgramFiles(x86)}/Android/android-sdk", "$env:LOCALAPPDATA/Android/Sdk", "$HOME/Library/Android/sdk", "$HOME/Android/Sdk")
        $adb = foreach ($root in $roots | Where-Object { $_ }) {
            foreach ($name in 'adb.exe', 'adb') {
                $candidate = Join-Path (Join-Path $root 'platform-tools') $name
                if (Test-Path $candidate -PathType Leaf) { $candidate; break }
            }
        }

        $adb = $adb | Select-Object -First 1
    }

    if (-not $adb) { throw 'adb not found: add the Android SDK platform-tools to PATH or set ANDROID_HOME.' }
    & $adb install -r $named
    if ($LASTEXITCODE -ne 0) { throw 'adb install failed.' }
}
