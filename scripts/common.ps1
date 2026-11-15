# Shared by the build scripts: paths, running dotnet, zipping. Dot-source it: . "$PSScriptRoot/common.ps1"

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:MauiProject = Join-Path $RepoRoot 'AmiiTag.Maui/AmiiTag.Maui.csproj'
$script:WebProject = Join-Path $RepoRoot 'AmiiTag.WebAssembly/AmiiTag.WebAssembly.csproj'
$script:ArtifactsRoot = Join-Path $RepoRoot 'artifacts'

# A plain function on purpose: a [Parameter()] attribute would make it an advanced function, whose common parameters
# (-OutVariable, -Verbose...) then swallow dotnet's short options such as -o and -v. MSBuild options with a colon
# (-p:Name=value, -t:Target) must be quoted: PowerShell would otherwise split them at the colon.
function Invoke-Dotnet {
    $arguments = @($args | ForEach-Object { $_ })
    Write-Host "dotnet $($arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) failed with exit code $LASTEXITCODE." }
}

# The workloads the MAUI project needs on this OS (MAUI, Android, iOS/Mac Catalyst...).
function Install-MauiWorkloads {
    Invoke-Dotnet workload restore $MauiProject
}

function New-CleanDirectory([string] $Path) {
    if (Test-Path $Path) { Remove-Item $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path $Path).Path
}

# Zips a folder's contents (or, with -IncludeFolder, the folder itself, e.g. an .app bundle).
function Compress-Folder([string] $Folder, [string] $Destination, [switch] $IncludeFolder) {
    if (Test-Path $Destination) { Remove-Item $Destination -Force }
    if ($IsMacOS) {
        # ditto keeps the symlinks and permissions an .app bundle needs.
        $dittoArgs = @('-c', '-k', '--sequesterRsrc')
        if ($IncludeFolder) { $dittoArgs += '--keepParent' }
        & ditto @dittoArgs $Folder $Destination
        if ($LASTEXITCODE -ne 0) { throw "ditto failed for $Folder." }
    }
    elseif ($IncludeFolder) {
        Compress-Archive -Path $Folder -DestinationPath $Destination
    }
    else {
        Compress-Archive -Path (Join-Path $Folder '*') -DestinationPath $Destination
    }

    Write-Host "Created $Destination" -ForegroundColor Green
}

# The application version (Android versionCode, iOS/Mac CFBundleVersion) for CI builds, so each one upgrades the last.
function Get-VersionArguments([int] $BuildNumber) {
    if ($BuildNumber -gt 0) { return @("-p:ApplicationVersion=$BuildNumber") }
    return @()
}

# The newest .app bundle the MAUI project built for a target framework and configuration.
function Find-BuiltApp([string] $TargetFramework, [string] $Configuration) {
    $bin = Join-Path $RepoRoot "AmiiTag.Maui/bin/$Configuration/$TargetFramework"
    $app = Get-ChildItem $bin -Filter '*.app' -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/]publish[\\/]' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $app) { throw "No .app under $bin." }
    return $app
}
