#Requires -Version 7
# control-server#535 re-review: pwsh mutations for S2 and S5, restored with git checkout (files committed first).
$ErrorActionPreference = 'Stop'
$mutations = [ordered]@{
    'S5 newest-by-@t check removed' = @('ParallelInstance.psm1', 'if ($null -ne $found -and $at -le $found.At) { continue }', '')
    'S2 production unread only warns' = @('ParallelInstance.psm1', "elseif (`$null -eq `$Effective -and (Get-MesIngestSource -Definition `$Definition) -cne 'production') { 'Warn' }", "elseif (`$null -eq `$Effective) { 'Warn' }")
    'S2 installer does not stop the service' = @('Install-ParallelInstanceLocal.ps1', 'Stop-Service -Name $serviceName -Force -ErrorAction Continue', '$null = $serviceName')
}
foreach ($name in $mutations.Keys) {
    $file, $from, $to = $mutations[$name]
    $path = Join-Path $PWD $file
    $text = [IO.File]::ReadAllText($path)
    $n = ([regex]::Matches($text, [regex]::Escape($from))).Count
    if ($n -ne 1) { "$name : NOT APPLIED (matches=$n)"; continue }
    [IO.File]::WriteAllText($path, $text.Replace($from, $to), [Text.UTF8Encoding]::new($false))
    $out = & pwsh -NoProfile -File ./Test-ParallelInstance.ps1 2>&1
    $code = $LASTEXITCODE
    & git checkout -- $file
    $first = ($out | Where-Object { "$_" -match '^\s+FAIL' } | Select-Object -First 2) -join ' / '
    "$name : exit=$code $($out | Select-Object -Last 1) :: $first"
}
& git status --short
