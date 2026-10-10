#Requires -Version 7
# control-server#262 S1 re-review, question 3, and what the mutation check turned up: PowerShell's
# string equality is a culture comparison that skips zero-width characters. Ordinal does not.
$zw = [char]0x200B
$pairs = [ordered]@{
    'map name, zero-width space inside'   = @("老厂前线${zw}new", '老厂前线new')
    'vehicle name, zero-width space after' = @("老厂前线新多仓位2$zw", '老厂前线新多仓位2')
    'key name, zero-width space after'    = @("agvId$zw", 'agvId')
    'map name, trailing ordinary space'   = @('老厂前线new ', '老厂前线new')
    'map name, capitals'                  = @('老厂前线NEW', '老厂前线new')
}
'{0,-40} {1,-6} {2,-6} {3,-10} {4,-8} {5}' -f 'pair', '-ceq', '-eq', '-ccontains', 'Ordinal', 'regex (Escape)'
foreach ($name in $pairs.Keys) {
    $a, $b = $pairs[$name]
    '{0,-40} {1,-6} {2,-6} {3,-10} {4,-8} {5}' -f $name, ($a -ceq $b), ($a -eq $b), (@($b) -ccontains $a),
        [string]::Equals($a, $b, [StringComparison]::Ordinal), ($a -match ('^' + [regex]::Escape($b) + '$'))
}
''
"pwsh $($PSVersionTable.PSVersion), culture '$([Globalization.CultureInfo]::CurrentCulture.Name)'"
