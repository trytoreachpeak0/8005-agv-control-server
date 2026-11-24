#Requires -Version 7

<#
.SYNOPSIS
    Checks the agent instruction pair: AGENTS.md holds the instructions, CLAUDE.md imports it (program#230).

.DESCRIPTION
    The layout is the one 8005-agv-protocol piloted in tools/check-agent-instructions.mjs (program#228), ported to
    PowerShell so a runner needs only git and pwsh:
      - AGENTS.md exists and is not empty;
      - the first non-empty line of CLAUDE.md is exactly "@AGENTS.md", so Claude Code loads the same text every other
        agent reads;
      - .gitattributes names both files explicitly with eol=lf, git resolves both to eol=lf, and the checked-out files
        carry no CR.
    Exit code 0 when all hold; 1 with one line per failure otherwise.
#>
[CmdletBinding()]
param(
    [string]$Repository = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pair = 'AGENTS.md', 'CLAUDE.md'
$failures = [Collections.Generic.List[string]]::new()

function Read-PairFile([string]$Name) {
    $file = Join-Path $Repository $Name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $failures.Add("$Name is missing"); return $null }
    return [IO.File]::ReadAllText($file, [Text.UTF8Encoding]::new($false))
}
function Get-TrimmedLines([string]$Text) {
    return @($Text.TrimStart([char]0xFEFF) -split "`n" | ForEach-Object { $_.TrimEnd("`r").Trim() })
}

$texts = @{}
foreach ($name in $pair) { $texts[$name] = Read-PairFile $name }

if ($null -ne $texts['AGENTS.md'] -and $texts['AGENTS.md'].Trim() -eq '') { $failures.Add('AGENTS.md is empty') }
if ($null -ne $texts['CLAUDE.md']) {
    $first = Get-TrimmedLines $texts['CLAUDE.md'] | Where-Object { $_ -ne '' } | Select-Object -First 1
    if ($first -cne '@AGENTS.md') {
        $failures.Add("CLAUDE.md must start with `"@AGENTS.md`", found $(ConvertTo-Json -InputObject ($first ?? '') -Compress)")
    }
}
foreach ($name in $pair) {
    if ($null -ne $texts[$name] -and $texts[$name].Contains("`r")) {
        $failures.Add("$name contains CR characters; it must be checked out with LF endings")
    }
}

$gitattributes = Read-PairFile '.gitattributes'
if ($null -ne $gitattributes) {
    $entries = @(Get-TrimmedLines $gitattributes | Where-Object { $_ -ne '' -and -not $_.StartsWith('#') } |
            ForEach-Object { , ($_ -split '\s+') })
    foreach ($name in $pair) {
        $named = @($entries | Where-Object { $_[0] -ceq $name -and @($_ | Select-Object -Skip 1) -ccontains 'eol=lf' })
        if ($named.Count -eq 0) { $failures.Add(".gitattributes must name $name explicitly with eol=lf") }
    }
}
$resolved = @(git -C $Repository check-attr eol -- @pair)
if ($LASTEXITCODE -ne 0) { $failures.Add("git check-attr failed with exit code $LASTEXITCODE") }
foreach ($name in $pair) {
    if (-not ($resolved | Where-Object { $_.Trim() -ceq "${name}: eol: lf" })) { $failures.Add("git does not resolve eol=lf for $name") }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { [Console]::Error.WriteLine("agent-instructions: $failure") }
    exit 1
}
Write-Output 'agent-instructions: ok'
exit 0
