<#
.SYNOPSIS
    Builds the TimedPlay ASF plugin.

.DESCRIPTION
    The plugin compiles against assemblies that ship inside ArchiSteamFarm itself, so they must be present in
    lib\ before building. If they are missing this script downloads the ASF generic release and extracts them.
    You can also point it at an existing ASF installation with -AsfDirectory instead.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -AsfVersion 6.3.10.3

.EXAMPLE
    .\build.ps1 -AsfDirectory C:\path\to\ASF
#>
[CmdletBinding()]
param(
    [string] $AsfVersion,
    [string] $AsfDirectory,
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$lib = Join-Path $root 'lib'

# Only these three are referenced by the project, and none of them are redistributed with the plugin.
$required = @('ArchiSteamFarm.dll', 'SteamKit2.dll', 'System.Composition.AttributedModel.dll')

function Test-LibReady {
    foreach ($name in $required) {
        if (-not (Test-Path (Join-Path $lib $name))) {
            return $false
        }
    }

    return $true
}

if ($AsfDirectory) {
    Write-Host "Using reference assemblies from $AsfDirectory" -ForegroundColor Cyan
}
elseif (-not (Test-LibReady)) {
    if (-not $AsfVersion) {
        $headers = @{ 'User-Agent' = 'TimedPlay-build' }

        # CI runners share an egress IP, so unauthenticated GitHub API calls get rate limited almost immediately.
        if ($env:GITHUB_TOKEN) {
            $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
        }

        try {
            $release = Invoke-RestMethod 'https://api.github.com/repos/JustArchiNET/ArchiSteamFarm/releases/latest' -Headers $headers
        }
        catch {
            throw "Could not query the latest ASF release from the GitHub API: $($_.Exception.Message)`nPass -AsfVersion explicitly to skip this lookup."
        }

        $AsfVersion = $release.tag_name
        $asset = $release.assets | Where-Object { $_.name -eq 'ASF-generic.zip' } | Select-Object -First 1

        if (-not $asset) {
            throw 'ASF-generic.zip was not found in the latest ASF release.'
        }

        $url = $asset.browser_download_url
    }
    else {
        $url = "https://github.com/JustArchiNET/ArchiSteamFarm/releases/download/$AsfVersion/ASF-generic.zip"
    }

    Write-Host "Downloading ASF $AsfVersion ..." -ForegroundColor Cyan

    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "ASF-generic-$AsfVersion.zip"
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "asf-ref-$AsfVersion"

    Invoke-WebRequest $url -OutFile $zip

    if (Test-Path $staging) {
        Remove-Item $staging -Recurse -Force
    }

    Expand-Archive $zip -DestinationPath $staging -Force

    New-Item -ItemType Directory -Force -Path $lib | Out-Null

    foreach ($name in $required) {
        Copy-Item (Join-Path $staging $name) $lib -Force
    }

    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host 'Reference assemblies are now in lib\ (these are gitignored).' -ForegroundColor Cyan
}

if ($NoBuild) {
    return
}

# When building from a release tag (v1.2.3) let the tag drive the assembly version.
$versionArgs = @()

if ($env:GITHUB_REF_NAME -match '^v(?<version>[0-9.]+)$') {
    $versionArgs = @("-p:AssemblyVersion=$($matches['version'])", "-p:FileVersion=$($matches['version'])")
}

& dotnet build (Join-Path $root 'TimedPlay.csproj') -c $Configuration @versionArgs

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

# Match on the configuration segment only - directory separators differ between Windows and Linux CI.
$dll = Get-ChildItem (Join-Path $root 'bin') -Recurse -Filter 'TimedPlay.dll' |
    Where-Object { $_.FullName -like "*$Configuration*" } |
    Select-Object -First 1

if (-not $dll) {
    throw 'Build reported success but TimedPlay.dll could not be found.'
}

Write-Host ''
Write-Host "Built $($dll.FullName)" -ForegroundColor Green
