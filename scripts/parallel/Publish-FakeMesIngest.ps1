#Requires -Version 7

<#
.SYNOPSIS
    Publishes ControlServer.FakeMesIngest self-contained and zips it for the parallel
    deployment. Runs on the control host.

.DESCRIPTION
    control-server#262. The double is not part of the WIRE_TO_GATE release package and should
    not be: the package is what goes to production vehicles, and adding a test double to it
    would widen the release identity that both ends verify. It travels as its own small zip
    instead, built from the same source tree as the server half so that contract discovery --
    which the server checks field by field -- agrees.

    Self-contained like Publish-ControlServer.ps1. factory01 does have a .NET SDK today, but
    the server half deliberately does not depend on that, and a double whose dependency graph
    is wider than the product's is a double that fails for reasons the product would not.

    Shape borrowed from Publish-ControlServer.ps1, including the clean-worktree refusal:
    publishing from a dirty tree produces a binary whose sourceCommit is a lie.

.PARAMETER OutputPath
    New directory for the publish output. A sibling <OutputPath>.zip and .sha256 are written
    beside it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = $env:WIRE_TO_GATE_DOTNET_EXE ?? 'dotnet'
$project = Join-Path $root 'tools\ControlServer.FakeMesIngest\ControlServer.FakeMesIngest.csproj'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputPath already exists; use a new empty destination: $resolvedOutput"
}

$status = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the source repository.' }
if ($status) { throw 'The source repository must be clean before publishing.' }

$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the source commit.'
}

# dotnet is invoked with the repository as the working directory so global.json selects the
# SDK global.json pins (no version written here: a copy of it goes stale the day the pin moves,
# and one already had). Run from anywhere else and a newer SDK is picked up, which reports
# compiler errors the pinned one does not.
Push-Location $root
try {
    & $dotnet restore $project --locked-mode --runtime $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnet publish $project --configuration Release --runtime $RuntimeIdentifier `
        --self-contained true --no-restore --output $resolvedOutput
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}

$manifest = [ordered]@{
    schemaVersion = 1
    product = '8005 AGV ControlServer FakeMesIngest'
    sourceCommit = $sourceCommit
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'fake-mes-ingest-manifest.json'),
    (ConvertTo-Json -InputObject $manifest -Depth 6),
    [Text.UTF8Encoding]::new($false))

$zipPath = "$resolvedOutput.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $resolvedOutput '*') -DestinationPath $zipPath
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$hash  $(Split-Path -Leaf $zipPath)`n", [Text.UTF8Encoding]::new($false))

Write-Output "Published FakeMesIngest: $zipPath"
Write-Output "Source commit: $sourceCommit"
Write-Output "SHA-256: $hash"
