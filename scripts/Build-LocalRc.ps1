#Requires -Version 7
<#
.SYNOPSIS
    Builds the WIRE_TO_GATE release package on the control host, without CI.

.DESCRIPTION
    The standby path for the CD pipeline. release.yml on win11-01 is the normal
    way to produce a release package; this script produces a byte-for-byte
    equivalent one locally when that runner cannot hand its artifact to GitHub.

    That is not hypothetical. On 2026-09-07 five consecutive release runs built
    successfully and then failed on the handoff -- CreateArtifact, FinalizeArtifact
    and a git fetch, all of them 21-second TCP connect timeouts to the proxy the
    guest was pointed at. The build half is local and reliable; only the network
    hop to GitHub is not. See issue #7.

    The output is what 15-deploy-control-server.ps1 -PackagePath expects: a zip,
    a .sha256 beside it, release-manifest.json and SHA256SUMS.txt. Deployment is
    unchanged -- this replaces `gh run download`, nothing else.

    Both repositories are read from throwaway clones, exactly as the workflow
    does. New-WireToGateReleaseCandidate.ps1 refuses a dirty ControlServer
    worktree, and the local one usually carries untracked evidence that is not
    ours to remove; cloning also means the package is built from committed state
    rather than from whatever happens to be in the working tree.

.PARAMETER OnboardCommit
    OnboardHmi commit to build, full 40-character hash. Same input release.yml
    takes.

.PARAMETER OnboardBranch
    Branch containing that commit.

.PARAMETER ControlServerRepo
    Source clone of this repository. Defaults to the one this script lives in.

.PARAMETER OnboardRepo
    Source clone of 8005-agv-onboard-hmi. Defaults to its sibling directory.
    A URL works too, at the cost of needing network access to fetch it.

.PARAMETER Root
    Scratch directory. Wiped on every run.

.EXAMPLE
    ./scripts/Build-LocalRc.ps1 -OnboardCommit <40-hex> -OnboardBranch OnboardHmi_MVP

.EXAMPLE
    # then deploy the server half from the zip it printed
    pwsh -File ../../remote-ops/factory-server/scripts/15-deploy-control-server.ps1 -PackagePath <zip>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $OnboardCommit,

    [string] $OnboardBranch = 'OnboardHmi_MVP',

    [string] $ControlServerRepo = (Split-Path -Parent $PSScriptRoot),

    [string] $OnboardRepo,

    [string] $Root = (Join-Path ([IO.Path]::GetTempPath()) 'wire-to-gate-local-rc')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

if (-not $OnboardRepo) {
    $OnboardRepo = Join-Path (Split-Path -Parent $ControlServerRepo) '8005-agv-onboard-hmi'
}
if (-not (Test-Path -LiteralPath $OnboardRepo) -and $OnboardRepo -notmatch '^[a-z]+://') {
    throw "OnboardHmi clone not found: $OnboardRepo. Pass -OnboardRepo with its path or URL."
}

function Invoke-Git([string[]]$Arguments, [string]$FailureMessage) {
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit $LASTEXITCODE): $($output -join '; ')"
    }
    return $output
}

$branch = (Invoke-Git @('-C', $ControlServerRepo, 'rev-parse', '--abbrev-ref', 'HEAD') 'Cannot read the current branch.').Trim()
$commit = (Invoke-Git @('-C', $ControlServerRepo, 'rev-parse', 'HEAD') 'Cannot read HEAD.').Trim()

# The package is built from HEAD, so anything uncommitted is silently not in it.
# Saying so is cheap; discovering it after a deployment is not.
$dirty = @(Invoke-Git @('-C', $ControlServerRepo, 'status', '--porcelain') 'Cannot read the working tree state.')
if ($dirty.Count -gt 0) {
    Write-Warning "ControlServer worktree has $($dirty.Count) uncommitted change(s). The package is built from HEAD ($($commit.Substring(0,7))); they are NOT in it."
}

if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root -Force | Out-Null

$mirror = Join-Path $Root 'control-server'
Write-Host "==> cloning ControlServer $branch @ $($commit.Substring(0,7)) into $mirror"
Invoke-Git @('clone', '--quiet', '--no-hardlinks', $ControlServerRepo, $mirror) 'ControlServer clone failed.' | Out-Null
Invoke-Git @('-C', $mirror, 'checkout', '--quiet', '--detach', $commit) 'ControlServer checkout failed.' | Out-Null

$out = Join-Path $Root ('wire-to-gate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "==> assembling release candidate into $out"
& (Join-Path $mirror 'scripts/New-WireToGateReleaseCandidate.ps1') `
    -OutputRoot $out `
    -OnboardCommit $OnboardCommit `
    -OnboardBranch $OnboardBranch `
    -OnboardRepositoryUrl $OnboardRepo

# Same layout and the same name shape as the Archive package step of release.yml,
# so a locally built package and a downloaded one are interchangeable downstream.
$stage = Join-Path $Root 'release-stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$name = 'WireToGate-{0}-{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')), $commit.Substring(0, 7)
$zip = Join-Path $stage "$name.zip"

Write-Host "==> archiving to $zip"
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$name.zip" | Set-Content -LiteralPath (Join-Path $stage "$name.zip.sha256") -Encoding ascii
Copy-Item (Join-Path $out 'release-manifest.json') $stage
Copy-Item (Join-Path $out 'SHA256SUMS.txt') $stage

$manifest = Get-Content -Raw (Join-Path $out 'release-manifest.json') | ConvertFrom-Json
[pscustomobject]@{
    zip           = $zip
    sha256        = $hash
    controlServer = $manifest.components.controlServer.commit
    onboardHmi    = $manifest.components.onboardHmi.commit
}
