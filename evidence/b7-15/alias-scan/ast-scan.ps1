param([string[]]$Files)
$aliases = @(Get-Alias | ForEach-Object Name)
$total = 0
foreach ($f in $Files) {
    $t = $null; $e = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$t, [ref]$e)
    $hits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and
            $n.CommandElements.Count -eq 1 -and $n.GetCommandName() -in $aliases -and
            -not ($n.Parent -is [System.Management.Automation.Language.PipelineAst] -and $n.Parent.PipelineElements.Count -gt 1) }, $true))
    foreach ($h in $hits) { "{0}:{1}: {2}" -f $f, $h.Extent.StartLineNumber, $h.Extent.Text }
    "{0} parseErrors={1} bareAliasCalls={2}" -f $f, @($e).Count, $hits.Count
    $total += $hits.Count
}
"TOTAL bareAliasCalls=$total"
