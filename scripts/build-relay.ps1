<#
.SYNOPSIS
    Publishes the NFC relay CLI (AmiiTag.NfcRelay) as one self-contained executable per runtime.

.DESCRIPTION
    Each runtime gets a single file with the .NET runtime and native libraries inside (no install needed), copied to
    artifacts/relay/amiitag-nfc-relay-<version>-<runtime>[.exe].

.EXAMPLE
    ./scripts/build-relay.ps1
    ./scripts/build-relay.ps1 -Runtime win-x64, win-arm64, linux-x64 -Version v1.2.0
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')] [string[]] $Runtime = @('win-x64'),
    [string] $Version
)

. "$PSScriptRoot/common.ps1"

$project = Join-Path $RepoRoot 'AmiiTag.NfcRelay/AmiiTag.NfcRelay.csproj'
$out = Join-Path $ArtifactsRoot 'relay'
New-Item -ItemType Directory -Path $out -Force | Out-Null

# Only the version properties a console app uses (Get-VersionArguments also covers the apps' build numbers).
$versionArguments = @(Get-VersionArguments 0 $Version)
$label = if ($Version) { '-' + ($Version.Trim() -replace '^[vV]', '') } else { '' }

foreach ($rid in $Runtime) {
    $publish = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/relay-$rid-$Configuration")
    Invoke-Dotnet publish $project -c $Configuration -r $rid --self-contained true --output $publish `
        '-p:PublishSingleFile=true' `
        '-p:IncludeNativeLibrariesForSelfExtract=true' `
        '-p:EnableCompressionInSingleFile=true' `
        '-p:DebugType=none' `
        @versionArguments

    $name = 'amiitag-nfc-relay'
    $extension = if ($rid.StartsWith('win-')) { '.exe' } else { '' }
    $exe = Join-Path $publish "$name$extension"
    if (-not (Test-Path $exe)) { throw "The publish didn't produce $exe." }

    # Everything should be inside the executable; anything left beside it would be missing from a copy of it alone.
    $extra = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.FullName -ne (Resolve-Path $exe).Path })
    if ($extra.Count -gt 0) {
        Write-Warning "These files weren't bundled into $name$extension ($rid):`n$(($extra | ForEach-Object { $_.FullName.Substring($publish.Length + 1) }) -join "`n")"
    }

    $target = Join-Path $out "$name$label-$rid$extension"
    Copy-Item $exe $target -Force
    Write-Host "Executable: $target" -ForegroundColor Green
}
