<#
.SYNOPSIS
    Publishes the Windows app as one self-contained exe (see Properties/PublishProfiles/win-*.pubxml).

.EXAMPLE
    ./scripts/build-windows.ps1
    ./scripts/build-windows.ps1 -Configuration Debug -Runtime win-arm64
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [int] $BuildNumber = 0,
    [string] $Version,
    [switch] $InstallWorkloads
)

. "$PSScriptRoot/common.ps1"
if (-not $IsWindows) { throw 'The Windows app can only be built on Windows.' }
if ($InstallWorkloads) { Install-MauiWorkloads }

$publish = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/windows-$Runtime-$Configuration")
Invoke-Dotnet publish $MauiProject -f net10.0-windows10.0.19041.0 -c $Configuration "-p:PublishProfile=$Runtime" "-p:PublishDir=$publish/" `
    @(Get-VersionArguments $BuildNumber $Version)

$out = Join-Path $ArtifactsRoot 'windows'
New-Item -ItemType Directory -Path $out -Force | Out-Null

# A single file publish: everything (wwwroot, WinUI resources, native libraries) is inside the exe and extracted on
# first run. Anything still beside it would be missing from a copy of the exe alone, so it's reported.
$exe = Join-Path $publish "$([System.IO.Path]::GetFileNameWithoutExtension($MauiProject)).exe"
if (-not (Test-Path $exe)) { throw "The publish didn't produce $exe." }
$extra = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.FullName -ne (Resolve-Path $exe).Path -and $_.Extension -ne '.pdb' })
if ($extra.Count -gt 0) {
    Write-Warning "These files weren't bundled into the exe:`n$(($extra | ForEach-Object { $_.FullName.Substring($publish.Length + 1) }) -join "`n")"
}

# Any name works: the exe carries resources.pri, which WinUI finds whatever the exe is called.
$target = Join-Path $out "AmiiTag-windows-$Runtime-$Configuration.exe"
Copy-Item $exe $target -Force
Write-Host "Exe: $target" -ForegroundColor Green
