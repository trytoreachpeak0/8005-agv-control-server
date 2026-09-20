#Requires -Version 7

<#
Says where a runner looked, so that "nothing was found" can be told apart from "nothing was found
there".

An error message can only describe what it observed. It has no way to know it was reading the wrong
directory, and "read nothing" is far more often a wrong location than a broken producer:
control-server#211 spent a whole G3 slot reading `No scenario reported a protocol release identity.`
as a protocol fault while the evidence sat one directory away, written by child processes that had
resolved a relative -EvidenceRoot against the stage tree.

A function rather than a few lines inline, for one reason: this text only runs on a failure path, and
a line that only runs when something has already gone wrong is exactly the kind that is never executed
and never noticed. Here Test-G3EvidenceLocation.ps1 drives THIS function -- not a copy of it lifted
into a test, which is a different thing than what ships and can pass while the shipped code is broken
(measured in control-server#203, where a lifted copy came out green on code that did not work).
#>

<#
.SYNOPSIS
One sentence naming the absolute directory that was read and what was in it.

.DESCRIPTION
Distinguishes three states the caller cannot otherwise tell apart: the directory does not exist, it
exists and is empty, it exists and holds N entries. Never throws -- it runs while another failure is
already being reported, and a diagnostic that replaces the failure it was explaining is worse than no
diagnostic at all.
#>
# English plurals for the handful of nouns this module is given: 'directory' -> 'directories',
# 'file' -> 'files', 'match' -> 'matches'. A bare + 's' produced "0 scenario directorys" in the first
# version, which Test-G3EvidenceLocation.ps1 caught -- a message nobody can read is a message nobody
# will trust when it matters.
function ConvertTo-G3Plural {
    param([Parameter(Mandatory)][string]$Noun)

    if ($Noun -cmatch '[^aeiou]y$') { return ($Noun -replace 'y$', 'ies') }
    if ($Noun -cmatch '(s|x|z|ch|sh)$') { return "${Noun}es" }
    return "${Noun}s"
}

function Get-G3EvidenceLocationDiagnostic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        # What a child entry is called in the message: 'scenario directory', 'evidence file', ...
        [string]$EntryNoun = 'entry',
        # Directories by default; -File counts files instead.
        [switch]$File)

    if ([string]::IsNullOrWhiteSpace($Path)) { return 'Looked in (no path was given).' }

    # Reported absolute even when the directory is gone, because a relative path in this sentence
    # reproduces the very ambiguity the sentence exists to end.
    #
    # The second argument is NOT redundant. A bare [IO.Path]::GetFullPath($Path) resolves against the
    # PROCESS working directory, which PowerShell never updates; Push-Location and Set-Location move
    # PowerShell's own location, and that is what Get-ChildItem below uses. So the bare call prints one
    # directory while the count comes from another, and a diagnostic that names the wrong place is
    # worse than one that names none.
    #
    # Worth knowing why this line exists at all: this module was written to fix a path being resolved
    # twice against two different directories (control-server#211) -- and its first version did exactly
    # that, one layer up. Measured, not noticed by reading: Test-G3EvidenceLocation.ps1's relative case
    # printed the repository root from inside a Push-Location. It had passed until then, because the
    # assertion only checked the path was rooted and ended in the right word, which the wrong answer
    # also satisfies.
    $shown = try { [System.IO.Path]::GetFullPath($Path, $PWD.ProviderPath) } catch { $Path }

    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return "Looked in '$shown' -- the directory does not exist."
        }
        $entries = @(Get-ChildItem -LiteralPath $Path -Directory:(-not $File) -File:$File -ErrorAction Stop)
        $noun = if ($entries.Count -eq 1) { $EntryNoun } else { ConvertTo-G3Plural $EntryNoun }
        return "Looked in '$shown' -- $($entries.Count) $noun."
    } catch {
        return "Looked in '$shown' -- could not be listed: $($_.Exception.Message)"
    }
}

Export-ModuleMember -Function Get-G3EvidenceLocationDiagnostic
