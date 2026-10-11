#Requires -Version 7
<#
Offline static checks over the repository's PowerShell (control-server#583). Scripts/Test-ScriptStaticChecks.ps1 runs
them against its fixtures and against scripts/; this module only scans and reports.

These are regression guards for the commonest shapes, not proofs. Each check's header lists what it cannot see.
#>

Set-StrictMode -Version Latest

<#
Parses every file with Parser::ParseFile and returns { Files; Findings = { File, Line, Column, Message } }.

ParseFile, not ParseInput with the errors thrown away: an AST check built on ParseInput keeps passing on a file that does
not parse at all (control-server#472, whose script failed on "$InstanceDefinitionPath:" and passed a dozen AST checks).
#>
function Test-ScriptParse {
    param([Parameter(Mandatory)][string[]]$Path)
    $findings = [System.Collections.Generic.List[object]]::new()
    $files = 0
    foreach ($file in $Path) {
        $files++
        $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors)
        foreach ($e in @($errors)) {
            $findings.Add([pscustomobject]@{
                File    = $file
                Line    = $e.Extent.StartLineNumber
                Column  = $e.Extent.StartColumnNumber
                Message = "$($e.ErrorId): $($e.Message)"
            })
        }
    }
    return [pscustomobject]@{ Files = $files; Findings = $findings.ToArray() }
}

Export-ModuleMember -Function Test-ScriptParse
