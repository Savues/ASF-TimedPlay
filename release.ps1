#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes a TimedPlay release entirely through the GitHub REST API.

.DESCRIPTION
    Builds the plugin, packages it, mirrors the working tree to GitHub and creates the annotated tag and
    Release - all with plain HTTPS calls. `git push` is never involved, so the script also works on networks
    where the Git smart-HTTP transport is blocked or too flaky to finish.

    The remote branch always ends up mirroring the working tree, one commit on top of whatever is currently
    on GitHub. Local commits are neither required nor created.

.PARAMETER Version
    Release version without the leading `v`, e.g. `1.4.0`. It is written into TimedPlay.csproj before the
    build and used for the tag and the Release name.

.PARAMETER Token
    GitHub personal access token. Defaults to $env:GITHUB_TOKEN and then to the credential git has stored
    for github.com.

.PARAMETER SkipBuild
    Reuse the DLL already present in bin\Release\net10.0 instead of rebuilding.

.PARAMETER SkipChecks
    Skip the trimmed-BCL check (only meaningful together with a build).

.EXAMPLE
    .\release.ps1 -Version 1.4.0

.EXAMPLE
    .\release.ps1 -Version 1.4.1 -SkipBuild
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Version,

	[string] $Token,

	[switch] $SkipBuild,

	[switch] $SkipChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$gitBase = @('-c', "safe.directory=$repoRoot")

function Invoke-Git {
	param([string[]] $Arguments)

	$output = & git ($gitBase + $Arguments) 2>&1

	if ($LASTEXITCODE -ne 0) {
		throw "git $($Arguments -join ' ') failed:`n$($output | Out-String)"
	}

	return $output
}

function Invoke-Api {
	param(
		[Parameter(Mandatory = $true)][string] $Method,
		[Parameter(Mandatory = $true)][string] $Uri,
		$Body
	)

	$splat = @{ Method = $Method; Uri = $Uri; Headers = $script:Headers }

	if ($PSBoundParameters.ContainsKey('Body') -and ($null -ne $Body)) {
		$splat['Body'] = ($Body | ConvertTo-Json -Depth 12)
		$splat['ContentType'] = 'application/json'
	}

	return Invoke-RestMethod @splat
}

# Version handling --------------------------------------------------------------------------------------------

$versionParts = @($Version.Split('.'))

while ($versionParts.Count -lt 4) {
	$versionParts += '0'
}

$assemblyVersion = ($versionParts[0..3] -join '.')
$tagName = "v$Version"

$csprojPath = Join-Path $repoRoot 'TimedPlay.csproj'
$csproj = [IO.File]::ReadAllText($csprojPath)
$csproj = [Text.RegularExpressions.Regex]::Replace($csproj, '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>")
$csproj = [Text.RegularExpressions.Regex]::Replace($csproj, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$assemblyVersion</FileVersion>")
[IO.File]::WriteAllText($csprojPath, $csproj)

Write-Host "Version $assemblyVersion / tag $tagName" -ForegroundColor Cyan

# Build -------------------------------------------------------------------------------------------------------

if (-not $SkipBuild) {
	Write-Host 'Building the plugin...' -ForegroundColor Cyan

	$buildArgs = @()

	if ($SkipChecks) {
		$buildArgs += '-NoBuild'
	}

	& (Join-Path $repoRoot 'build.ps1') @buildArgs

	if ($LASTEXITCODE -ne 0) {
		throw 'build.ps1 failed.'
	}
}

$dllPath = Join-Path $repoRoot 'bin\Release\net10.0\TimedPlay.dll'

if (-not (Test-Path $dllPath)) {
	throw "Build output not found: $dllPath"
}

# Package -----------------------------------------------------------------------------------------------------

$zipPath = Join-Path $repoRoot 'TimedPlay.zip'

if (Test-Path $zipPath) {
	Remove-Item -LiteralPath $zipPath -Force
}

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('timedplay-pkg-' + [Guid]::NewGuid().ToString('N'))
$pluginDir = Join-Path $stageRoot 'plugins\TimedPlay'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $dllPath -Destination $pluginDir

foreach ($extra in @('LICENSE', 'README.md')) {
	$extraPath = Join-Path $repoRoot $extra

	if (Test-Path $extraPath) {
		Copy-Item -LiteralPath $extraPath -Destination $pluginDir
	}
}

Compress-Archive -Path (Join-Path $stageRoot 'plugins') -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stageRoot -Recurse -Force

Write-Host "Packaged $zipPath" -ForegroundColor Cyan

# Authentication ----------------------------------------------------------------------------------------------

if (-not $Token) {
	$Token = $env:GITHUB_TOKEN
}

if (-not $Token) {
	Write-Host 'No token supplied, asking git for the github.com credential...' -ForegroundColor Yellow

	$credential = @('protocol=https', 'host=github.com', '') | git credential fill 2>$null
	$passwordLine = @($credential | Where-Object { $_ -like 'password=*' })[0]

	if ($passwordLine) {
		$Token = $passwordLine.Substring('password='.Length)
	}
}

if (-not $Token) {
	throw 'No GitHub token. Pass -Token, set $env:GITHUB_TOKEN, or store a credential for github.com.'
}

$remoteUrl = (Invoke-Git @('remote', 'get-url', 'origin') | Select-Object -First 1)

if ($remoteUrl -notmatch 'github\.com[:/](?<slug>[^/]+/[^/]+?)(?:\.git)?$') {
	throw "Cannot parse a GitHub repository out of the origin remote: $remoteUrl"
}

$slug = $Matches['slug']

$script:Headers = @{
	Authorization = "Bearer $Token"
	Accept = 'application/vnd.github+json'
	'User-Agent' = 'TimedPlay-release-script'
}

$apiBase = "https://api.github.com/repos/$slug"

Write-Host "Publishing to $slug" -ForegroundColor Cyan

# Mirror the working tree -------------------------------------------------------------------------------------

$repoInfo = Invoke-Api -Method Get -Uri $apiBase
$branch = $repoInfo.default_branch
$headCommit = Invoke-Api -Method Get -Uri "$apiBase/commits/$branch"

$tracked = @(Invoke-Git @('ls-files') | Where-Object { $_ })

Write-Host "Uploading $($tracked.Count) file(s)..." -ForegroundColor Cyan

$treeEntries = @()

foreach ($file in $tracked) {
	$fullPath = Join-Path $repoRoot $file
	$content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($fullPath))
	$blob = Invoke-Api -Method Post -Uri "$apiBase/git/blobs" -Body @{ content = $content; encoding = 'base64' }
	$treeEntries += @{ path = $file; mode = '100644'; type = 'blob'; sha = $blob.sha }
}

$tree = Invoke-Api -Method Post -Uri "$apiBase/git/trees" -Body @{ tree = $treeEntries }
$commit = Invoke-Api -Method Post -Uri "$apiBase/git/commits" -Body @{
	message = "release: $tagName"
	tree = $tree.sha
	parents = @($headCommit.sha)
}

Invoke-Api -Method Patch -Uri "$apiBase/git/refs/heads/$branch" -Body @{ sha = $commit.sha; force = $false } | Out-Null

Write-Host "Branch $branch is now at $($commit.sha.Substring(0, 7))" -ForegroundColor Cyan

# Tag ---------------------------------------------------------------------------------------------------------

$tagObject = Invoke-Api -Method Post -Uri "$apiBase/git/tags" -Body @{
	tag = $tagName
	message = "TimedPlay $tagName"
	object = $commit.sha
	type = 'commit'
}

$existingTag = $null

try {
	$existingTag = Invoke-Api -Method Get -Uri "$apiBase/git/ref/tags/$tagName"
}
catch {
	$existingTag = $null
}

if ($existingTag) {
	Invoke-Api -Method Patch -Uri "$apiBase/git/refs/tags/$tagName" -Body @{ sha = $tagObject.sha; force = $true } | Out-Null
}
else {
	Invoke-Api -Method Post -Uri "$apiBase/git/refs" -Body @{ ref = "refs/tags/$tagName"; sha = $tagObject.sha } | Out-Null
}

Write-Host "Tag $tagName points at $($commit.sha.Substring(0, 7))" -ForegroundColor Cyan

# Release -----------------------------------------------------------------------------------------------------

$releaseBody = @"
TimedPlay $tagName

Built from commit $($commit.sha.Substring(0, 7)).
"@

$existingRelease = $null

try {
	$existingRelease = Invoke-Api -Method Get -Uri "$apiBase/releases/tags/$tagName"
}
catch {
	$existingRelease = $null
}

if ($existingRelease) {
	$release = Invoke-Api -Method Patch -Uri "$apiBase/releases/$($existingRelease.id)" -Body @{ name = $tagName; body = $releaseBody }

	foreach ($asset in @($existingRelease.assets)) {
		if ($asset.name -eq 'TimedPlay.zip') {
			Invoke-Api -Method Delete -Uri "$apiBase/releases/assets/$($asset.id)" | Out-Null
		}
	}
}
else {
	$release = Invoke-Api -Method Post -Uri "$apiBase/releases" -Body @{
		tag_name = $tagName
		name = $tagName
		body = $releaseBody
		draft = $false
		prerelease = $false
	}
}

$uploadUri = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=TimedPlay.zip"
$asset = Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $script:Headers -Body ([IO.File]::ReadAllBytes($zipPath)) -ContentType 'application/zip'

Write-Host "Released $tagName -> $($release.html_url)" -ForegroundColor Green
Write-Host "Asset: $($asset.browser_download_url)" -ForegroundColor Green
