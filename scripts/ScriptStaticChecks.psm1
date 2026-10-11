#Requires -Version 7
<#
Offline static checks over the repository's PowerShell (control-server#583). Stub: the self-check comes first.
#>

Set-StrictMode -Version Latest

function Test-ScriptParse {
    param([Parameter(Mandatory)][string[]]$Path)
    throw 'Test-ScriptParse: not implemented'
}

Export-ModuleMember -Function Test-ScriptParse
