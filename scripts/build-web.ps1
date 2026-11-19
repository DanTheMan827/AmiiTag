<#
.SYNOPSIS
    Publishes the WebAssembly app as a static site, ready for GitHub Pages or any static host.

.PARAMETER BasePath
    The path the site is served from, e.g. "/AmiiTag/" for https://user.github.io/AmiiTag/. Defaults to "/".

.EXAMPLE
    ./scripts/build-web.ps1
    ./scripts/build-web.ps1 -Configuration Debug -BasePath /AmiiTag/
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $BasePath = '/',
    [string] $OutputDir
)

. "$PSScriptRoot/common.ps1"

if (-not $BasePath.StartsWith('/')) { $BasePath = "/$BasePath" }
if (-not $BasePath.EndsWith('/')) { $BasePath = "$BasePath/" }
if (-not $OutputDir) { $OutputDir = Join-Path $ArtifactsRoot "web/$Configuration" }

$publish = New-CleanDirectory (Join-Path $ArtifactsRoot "obj/web-$Configuration")
Invoke-Dotnet publish $WebProject -c $Configuration -o $publish

# The site itself is the publish output's wwwroot.
$site = New-CleanDirectory $OutputDir
Copy-Item (Join-Path $publish 'wwwroot/*') $site -Recurse -Force

# Served from a sub folder: the app resolves everything (and its routes) against <base href>.
$index = Join-Path $site 'index.html'
$html = (Get-Content $index -Raw) -replace '<base href="[^"]*"\s*/?>', "<base href=`"$BasePath`" />"
Set-Content $index $html -NoNewline -Encoding utf8NoBOM

# GitHub Pages: unknown paths (a deep link like /AmiiTag/tag/...) get 404.html, which starts the app at that route;
# and without .nojekyll it would leave out _framework and _content (Jekyll skips folders starting with "_").
Copy-Item $index (Join-Path $site '404.html') -Force
New-Item -ItemType File -Path (Join-Path $site '.nojekyll') -Force | Out-Null

Write-Host "Web app ($Configuration, base $BasePath): $site" -ForegroundColor Green
