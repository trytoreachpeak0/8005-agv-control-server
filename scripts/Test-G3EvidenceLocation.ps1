#Requires -Version 7
<#
.SYNOPSIS
Drives Get-G3EvidenceLocationDiagnostic through every state it exists to tell apart.

.DESCRIPTION
The function runs on a failure path only -- a G3 runner calls it when it has already decided it found
nothing. A line that runs only after something else has gone wrong is never exercised by a green run,
so nothing but this check ever executes it. That is the whole reason it is here.

It drives the shipped function, not a copy lifted into this file. A lifted copy is a different thing
than what ships and can come out green on code that does not work -- measured in control-server#203,
where the copy sat outside the module and so had none of the resolution the test existed to check.

The last two cases are the ones with teeth. Telling "the directory is empty" from "the directory is
not there" is the distinction control-server#211 needed and did not have: its directory existed, and
was empty, because the child processes had written somewhere else. And the diagnostic may never throw
-- it runs while another failure is being reported, and a diagnostic that replaces the failure it was
explaining is worse than none.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'G3EvidenceLocation.psm1') -Force

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "g3-evidence-location-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force

$empty = Join-Path $scratch 'empty'
$null = New-Item -ItemType Directory -Path $empty -Force

$populated = Join-Path $scratch 'populated'
foreach ($name in 'real-onboard-normal-load', 'real-onboard-clock-skew') {
    $null = New-Item -ItemType Directory -Path (Join-Path $populated $name) -Force
}
$null = New-Item -ItemType File -Path (Join-Path $populated 'run-result.json') -Force

$single = Join-Path $scratch 'single'
$null = New-Item -ItemType Directory -Path (Join-Path $single 'only-one') -Force

$missing = Join-Path $scratch 'never-created'

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

$emptyText = Get-G3EvidenceLocationDiagnostic -Path $empty -EntryNoun 'scenario directory'
Add-Case 'an empty directory is reported as empty, and the path is named' `
    ($emptyText -like "*$empty*" -and $emptyText -like '*0 scenario directories*') $emptyText

$missingText = Get-G3EvidenceLocationDiagnostic -Path $missing -EntryNoun 'scenario directory'
Add-Case 'a directory that was never created is NOT reported as empty' `
    ($missingText -like '*does not exist*' -and $missingText -notlike '*0 scenario*') $missingText

$populatedText = Get-G3EvidenceLocationDiagnostic -Path $populated -EntryNoun 'scenario directory'
Add-Case 'directories are counted and the loose file beside them is not' `
    ($populatedText -like '*2 scenario directories*') $populatedText

$singleText = Get-G3EvidenceLocationDiagnostic -Path $single -EntryNoun 'scenario directory'
Add-Case 'one entry reads as one, not as "1 scenario directorys"' `
    ($singleText -like '*1 scenario directory.*') $singleText

$fileText = Get-G3EvidenceLocationDiagnostic -Path $populated -EntryNoun 'evidence file' -File
Add-Case '-File counts files instead of directories' `
    ($fileText -like '*1 evidence file.*') $fileText

# Absolute, always: a relative path in this sentence reproduces the ambiguity it exists to end.
#
# The assertion is that the printed path IS $empty, not merely that it is rooted and ends in the right
# word. The weaker version passed while the function printed the repository root -- Push-Location moves
# PowerShell's location, [IO.Path]::GetFullPath resolves against the process working directory, which
# PowerShell never updates, and both answers are rooted and both end in 'empty'.
Push-Location $scratch
try {
    $relativeText = Get-G3EvidenceLocationDiagnostic -Path 'empty' -EntryNoun 'scenario directory'
} finally { Pop-Location }
$quoted = ($relativeText -replace "^Looked in '", '') -replace "' --.*$", ''
Add-Case 'a relative path is resolved against PowerShell''s location, not the process working directory' `
    ($quoted -ieq $empty) "$relativeText   (expected path '$empty')"

# It is called while another failure is already being reported, so it may not raise one of its own.
$threw = $false
try { $null = Get-G3EvidenceLocationDiagnostic -Path ([string]::new([char]0, 1)) -EntryNoun 'scenario directory' }
catch { $threw = $true }
Add-Case 'an unusable path is answered, not thrown' (-not $threw) "threw=$threw"

$blankText = Get-G3EvidenceLocationDiagnostic -Path '' -EntryNoun 'scenario directory'
Add-Case 'an empty path says so rather than pointing at the current directory' `
    ($blankText -like '*no path was given*') $blankText

Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "G3EvidenceLocation self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "G3EvidenceLocation self-check: all $($results.Count) cases as expected."
