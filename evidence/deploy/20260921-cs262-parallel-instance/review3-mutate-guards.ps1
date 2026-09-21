#Requires -Version 7
# Mutation check for the control-server#262 re-review fixes, round 3 (S1 re-review: M1 and
# questions 2-4), plus the round-2 mutations with their anchors moved to the current code. Each mutation removes one guard in
# a throwaway copy of scripts/parallel (inside the repository, so the C# drift guard still finds
# src/), runs the self-test there, and reports which cases went red. The copy is deleted and the
# real files' blob hashes are compared before and after.
param([Parameter(Mandatory)][string] $Repo)
$ErrorActionPreference = 'Stop'
$src = Join-Path $Repo 'scripts\parallel'
$mut = Join-Path $Repo 'scripts\_mutation-parallel'
$before = & git -C $Repo hash-object (Get-ChildItem $src -File | ForEach-Object FullName)

$mutations = [ordered]@{
    # ---- round 2, re-anchored where the code moved ----
    'r2 S1-3 service refusal no longer aborts' = @{
        File = 'ParallelInstance.psm1'
        Old = 'return & $result $true "the service step failed, so nothing else was removed: $($_.Exception.Message)"'
        New = '$failed.Add("service: $($_.Exception.Message)")'
    }
    'r2 S1 path layers removed from the sequence' = @{
        File = 'ParallelInstance.psm1'
        Old = '            $refusal = Get-ParallelInstanceDeleteRefusal -Path $target'
        New = '            $refusal = $null'
    }
    'r2 S1-2 allowlist marker check removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if ($leaf -notmatch $script:InstanceMarkerPattern) {'
        New = '    if ($false) {'
    }
    'r2 M3 map-25 id refusal removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '            (ConvertTo-IntegerOrNull $node[''mapId'']) -eq $script:ProductionMapId) {'
        New = '            $false) {'
    }
    # ---- round 3: M1, the service step fails closed ----
    'r3 M1 exit-code check removed' = @{
        File = 'ParallelHost.psm1'
        Old = '    if ($exitCode -ne 0) {'
        New = '    if ($false) {'
    }
    'r3 M1 result-file existence check removed' = @{
        File = 'ParallelHost.psm1'
        Old = '    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {'
        New = '    if ($false) {'
    }
    'r3 M1 PASS check removed' = @{
        File = 'ParallelHost.psm1'
        Old = '    if ([string] $result[''result''] -cne ''PASS'') {'
        New = '    if ($false) {'
    }
    'r3 M1 stale-result check removed' = @{
        File = 'ParallelHost.psm1'
        Old = '    if (Test-Path -LiteralPath $ResultPath) {'
        New = '    if ($false) {'
    }
    'r3 M1 uninstaller calls the product script directly again' = @{
        File = 'Uninstall-ParallelInstanceLocal.ps1'
        Old = '        Invoke-ParallelProductUninstaller -UninstallerPath $productUninstaller -ServiceName $item.Name `'
        New = '        & $productUninstaller -ServiceName $item.Name `'
    }
    'r3 action output leaks into the sequence result' = @{
        File = 'ParallelInstance.psm1'
        Old = '            $null = & $Actions.Service $item'
        New = '            & $Actions.Service $item'
    }
    # ---- round 3: question 2, junctions ----
    'r3 Q2 sequence ignores the link probe' = @{
        File = 'ParallelInstance.psm1'
        Old = '            try { $isLink = [bool](& $Actions.ReparsePoint $target) } catch { $isLink = $true }'
        New = '            $isLink = $false'
    }
    'r3 Q2 a throwing link probe counts as "not a link"' = @{
        File = 'ParallelInstance.psm1'
        Old = '            try { $isLink = [bool](& $Actions.ReparsePoint $target) } catch { $isLink = $true }'
        New = '            try { $isLink = [bool](& $Actions.ReparsePoint $target) } catch { $isLink = $false }'
    }
    'r3 Q2 required-action check removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if ($missing.Count -gt 0) {'
        New = '    if ($false) {'
    }
    'r3 Q2 Remove-ParallelInstanceDirectory skips the link check' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if (Test-ParallelInstanceReparsePoint -Path $Path) {'
        New = '    if ($false) {'
    }
    # ---- round 3: question 4, the installer's deletes ----
    'r3 Q4 installer deletes the previous generation with a bare Remove-Item' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '    Remove-ParallelInstanceDirectory -Path $previousRoot'
        New = '    if (Test-Path -LiteralPath $previousRoot) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }'
    }
    # ---- round 3: question 3, map names ----
    'r3 Q3 map comparison key no longer normalises' = @{
        File = 'ParallelInstance.psm1'
        Old = '    $key = $Value.Normalize([Text.NormalizationForm]::FormKC)'
        New = '    return $Value'
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
