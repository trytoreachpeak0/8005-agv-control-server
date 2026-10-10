#Requires -Version 7
# Mutation check for the control-server#262 re-review fixes. Each mutation removes one guard in
# a throwaway copy of scripts/parallel (inside the repository, so the C# drift guard still finds
# src/), runs the self-test there, and reports which cases went red. The copy is deleted and the
# real files' blob hashes are compared before and after.
param([Parameter(Mandatory)][string] $Repo)
$ErrorActionPreference = 'Stop'
$src = Join-Path $Repo 'scripts\parallel'
$mut = Join-Path $Repo 'scripts\_mutation-parallel'
$before = & git -C $Repo hash-object (Get-ChildItem $src -File | ForEach-Object FullName)

$mutations = [ordered]@{
    'S1-3 service refusal no longer aborts' = @{
        File = 'ParallelInstance.psm1'
        Old = 'return & $result $true "the service step failed, so nothing else was removed: $($_.Exception.Message)"'
        New = '$failed.Add("service: $($_.Exception.Message)")'
    }
    'S1 directory guards removed from the sequence' = @{
        File = 'ParallelInstance.psm1'
        Old = '            $owned = Test-OwnedPath $target'
        New = '            $owned = $null; if ($false) { }'
        Old2 = '            if (Test-ParallelInstancePathIsProduction -Path $target) {'
        New2 = '            if ($false) {'
    }
    'S1-1 forward slash no longer refused (GetFullPath check also removed)' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if ($Path.Contains(''/'')) { return "uses ''/''; write the path with backslashes only" }'
        New = ''
        Old2 = '    if ($full -cne $Path) { return "is not in canonical form (Windows reads it as ''$full'')" }'
        New2 = ''
    }
    'S1-2 allowlist marker check removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if ($leaf -notmatch $script:InstanceMarkerPattern) {'
        New = '    if ($false) {'
    }
    'M1 wildcard refusal removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if ($Name.IndexOfAny([char[]] ''*?[]'') -ge 0) {'
        New = '    if ($false) {'
    }
    'M3 map-25 id refusal removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '            (ConvertTo-IntegerOrNull $node[''mapId'']) -eq $script:ProductionMapId) {'
        New = '            $false) {'
    }
}

foreach ($name in $mutations.Keys) {
    $m = $mutations[$name]
    if (Test-Path $mut) { Remove-Item $mut -Recurse -Force }
    Copy-Item $src $mut -Recurse
    $file = Join-Path $mut $m.File
    $text = [IO.File]::ReadAllText($file)
    $hits = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
    $mutated = $text.Replace($m.Old, $m.New)
    if ($m.ContainsKey('Old2')) {
        $hits2 = ([regex]::Matches($mutated, [regex]::Escape($m.Old2))).Count
        $mutated = $mutated.Replace($m.Old2, $m.New2)
    } else { $hits2 = '-' }
    [IO.File]::WriteAllText($file, $mutated, [Text.UTF8Encoding]::new($false))
    # The injection must have landed: compare the file with the original.
    $landed = (Get-FileHash $file).Hash -ne (Get-FileHash (Join-Path $src $m.File)).Hash
    $out = @(& pwsh -NoProfile -File (Join-Path $mut 'Test-ParallelInstance.ps1') 2>&1 | ForEach-Object { "$_" })
    $red = @($out | Where-Object { $_ -match '^\s+FAIL\s' } | ForEach-Object { ($_ -replace '^\s+FAIL\s+', '') })
    Write-Host ''
    Write-Host "=== $name   (anchor hits: $hits / $hits2, landed: $landed)"
    Write-Host "    summary: $(($out | Where-Object { $_ -match 'passed, \d+ failed' }) -join ' ')"
    $red | ForEach-Object { Write-Host "    RED  $_" }
    if ($red.Count -eq 0) { Write-Host '    (nothing went red -- the guard is not covered)' }
}
Remove-Item $mut -Recurse -Force
$after = & git -C $Repo hash-object (Get-ChildItem $src -File | ForEach-Object FullName)
Write-Host ''
Write-Host "real files unchanged: $((Compare-Object $before $after) -eq $null)   mutation dir removed: $(-not (Test-Path $mut))"
