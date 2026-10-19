<#
.SYNOPSIS
    Verifies the built plugin against a trimmed ASF.

.DESCRIPTION
    The Windows release of ASF is a trimmed single-file build. ILLink keeps only the BCL members ASF itself
    calls, so anything the plugin calls that ASF never calls is silently removed and then throws
    MissingMethodException the first time it runs. A normal build against the framework-dependent release cannot
    detect this, because the development-time BCL is untrimmed.

    This script compares the plugin's call sites against the assemblies that actually survived trimming and fails
    the build when the plugin references something that is gone.

.PARAMETER TrimmedDirectory
    A directory holding a trimmed ASF - it must contain System.Private.CoreLib.dll, which the
    framework-dependent release does not ship.

.EXAMPLE
    .\check-trimming.ps1 -TrimmedDirectory C:\path\to\trimmed\asf
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TrimmedDirectory,

    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$tool = Join-Path $root 'tools/ilcheck'
$trimmed = (Resolve-Path $TrimmedDirectory).Path

if (-not (Test-Path (Join-Path $trimmed 'System.Private.CoreLib.dll'))) {
    throw "$trimmed does not look like a trimmed ASF - System.Private.CoreLib.dll is missing."
}

$dll = Get-ChildItem (Join-Path $root 'bin') -Recurse -Filter 'TimedPlay.dll' |
    Where-Object { $_.FullName -like "*$Configuration*" } |
    Select-Object -First 1

if (-not $dll) {
    throw "TimedPlay.dll was not found. Run build.ps1 before this script."
}

Write-Host 'Building the trimming checker ...' -ForegroundColor Cyan
& dotnet build $tool -c Release -v quiet --nologo

if ($LASTEXITCODE -ne 0) {
    throw "Could not build the trimming checker (exit code $LASTEXITCODE)."
}

Write-Host ''
Write-Host "Checking $($dll.Name) against $trimmed" -ForegroundColor Cyan

$checker = Get-ChildItem (Join-Path $tool 'bin') -Recurse -Filter 'ilcheck.dll' | Select-Object -First 1

& dotnet $checker.FullName $trimmed $dll.FullName

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'The plugin calls BCL members that ASF''s trimming removes - they will throw MissingMethodException at runtime.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'No trimmed-away BCL members. The plugin is safe on a trimmed ASF build.' -ForegroundColor Green
