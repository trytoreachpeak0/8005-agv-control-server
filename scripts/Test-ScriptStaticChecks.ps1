#Requires -Version 7
<#
.SYNOPSIS
    Offline static checks over the repository's PowerShell, and their self-check (control-server#583).

.DESCRIPTION
    Pure parsing, no rig, no window, no database, seconds. Each check runs twice: once against small fixtures written to
    %TEMP% here, which it must flag (and, for the counterexamples, must leave alone), and once against scripts/ itself,
    which it must leave clean AND must have read at least a known number of things -- a scan that broke would otherwise
    report zero findings for ever, which is also what success looks like.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-ScriptStaticChecks.ps1

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-ScriptStaticChecks.ps1 -SkipSelfTest -Path <file>, <file>
    Scans only the given files (the red evidence on bc22cfd25^ was taken this way).
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot,

    # Scan these files instead of every script under ScriptRoot. The lower bounds on what was scanned apply only to the
    # full scan.
    [string[]]$Path,

    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScriptStaticChecks.psm1') -Force

$cases = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $cases.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}
function Format-Finding([object[]]$Findings) {
    return (@($Findings | ForEach-Object { "$([IO.Path]::GetFileName($_.File)):$($_.Line) $($_.Message)" }) -join ' | ')
}

if (-not $SkipSelfTest) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "script-static-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $scratch -Force
    try {
        function New-Fixture([string]$Name, [string]$Body) {
            $file = Join-Path $scratch $Name
            Set-Content -LiteralPath $file -Encoding utf8NoBOM -Value $Body
            return $file
        }

        # --- 1. every script parses -------------------------------------------------------------------------------------
        # The cs#472 shape: "$name:" is a drive-qualified variable, and the whole file fails to parse.
        $broken = New-Fixture 'parse-broken.ps1' "`$x = 1`n`$y = 2`nWrite-Host `"value `$InstanceDefinitionPath: done`"`n"
        $clean = New-Fixture 'parse-clean.ps1' "`$x = 1`nWrite-Host `"value `${InstanceDefinitionPath}: done`"`n"
        $parse = Test-ScriptParse -Path $broken, $clean
        $hit = @($parse.Findings | Where-Object { $_.File -eq $broken })
        Add-Case 'parse: a syntax error is reported with its file and line' ($hit.Count -ge 1 -and $hit[0].Line -eq 3) (Format-Finding $parse.Findings)
        Add-Case 'parse: a clean file is not reported' (@($parse.Findings | Where-Object { $_.File -eq $clean }).Count -eq 0) (Format-Finding $parse.Findings)
        Add-Case 'parse: both files were read' ($parse.Files -eq 2) "$($parse.Files)"
    } finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- the repository ---------------------------------------------------------------------------------------------------
$full = -not $Path
$targets = if ($full) {
    @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -File -Include '*.ps1', '*.psm1' | ForEach-Object FullName)
} else {
    @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).ProviderPath })
}

$parse = Test-ScriptParse -Path $targets
Write-Host "parse: $($parse.Files) files read, $(@($parse.Findings).Count) with errors"
foreach ($f in $parse.Findings) { Write-Host "  PARSE  $($f.File):$($f.Line):$($f.Column) $($f.Message)" }
Add-Case 'repository: every script parses' (@($parse.Findings).Count -eq 0) "$(@($parse.Findings).Count) errors"
# 231 .ps1/.psm1 under scripts/ on fp/v2-impl@da6e40e2; the bound leaves room to delete some, not to lose the glob.
if ($full) { Add-Case 'repository: parse read at least 200 scripts' ($parse.Files -ge 200) "$($parse.Files)" }

$failed = @($cases | Where-Object { -not $_.Ok })
foreach ($c in $cases) { Write-Host ("{0}  {1}{2}" -f ($c.Ok ? 'ok  ' : 'FAIL'), $c.Name, ($c.Ok ? '' : " -- $($c.Actual)")) }
Write-Host "$($cases.Count - $failed.Count)/$($cases.Count) passed"
exit ($failed.Count -eq 0 ? 0 : 1)
