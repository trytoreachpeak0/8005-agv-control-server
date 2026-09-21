#Requires -Version 7
# Mutation check for the control-server#262 re-review fixes, round 8 (S1 re-review rounds 2 to 4, the
# zero-width allowlist fix, the secrets file's lifetime, one deletion scan, runtime premise), plus the round-2 mutations with their anchors moved to the current code. Each mutation removes one guard in
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
        Old = '        throw "Refusing to delete ''$Path'': it is a junction or symbolic link, not a directory this instance created."'
        New = '        $null = 0'
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
    # ---- round 4: allowlists compare Ordinal (zero-width lookalikes) ----
    'r4 config-key allowlist back to -ccontains' = @{
        File = 'ParallelInstance.psm1'
        Old = '        if (@($allowed | Where-Object { [string]::Equals($_, $key, [StringComparison]::Ordinal) }).Count -gt 0) { continue }'
        New = '        if ($allowed -ccontains $key) { continue }'
    }
    'r4 vehicleKey match back to -eq' = @{
        File = 'ParallelInstance.psm1'
        Old = 'Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::Ordinal) }'
        New = 'Where-Object { $_.VehicleKey -eq $vehicleKey }'
    }
    'r4 agvId pair check back to -cne' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if (-not [string]::Equals($agvId, $match.AgvId, [StringComparison]::Ordinal)) {'
        New = '    if ($agvId -cne $match.AgvId) {'
    }
    'r4 allowed-parent check back to -ieq' = @{
        File = 'ParallelInstance.psm1'
        Old = 'Where-Object { [string]::Equals($_, $parent, [StringComparison]::OrdinalIgnoreCase) }'
        New = 'Where-Object { $_ -ieq $parent }'
    }
    'r4 self-test landed-guard back to -eq' = @{
        File = 'Test-ParallelInstance.ps1'
        Old = '    if ([string]::Equals((Get-Fingerprint $mutated), $baselineFingerprint, [StringComparison]::Ordinal)) {'
        New = '    if ((Get-Fingerprint $mutated) -eq $baselineFingerprint) {'
    }
    # ---- round 5: S1 re-review round 3 (scanners, config path, GUID, premise, map separators) ----
    'r5 uninstaller calls the product script through a dynamic command' = @{
        File = 'Uninstall-ParallelInstanceLocal.ps1'
        Old = '        Invoke-ParallelProductUninstaller -UninstallerPath (Get-ParallelProductUninstallerPath -Layout $layout -ScriptRoot $PSScriptRoot) `'
        New = '        & (Get-ParallelProductUninstallerPath -Layout $layout -ScriptRoot $PSScriptRoot) `'
    }
    'r5 uninstaller names the product script' = @{
        File = 'Uninstall-ParallelInstanceLocal.ps1'
        Old = '        Test-ParallelInstanceReparsePoint -Path $path'
        New = '        Test-ParallelInstanceReparsePoint -Path $path # see Uninstall-ControlServerLocal.ps1'
    }
    'r5 installer deletes the config with Remove-Item' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '        $residue = Remove-ParallelInstanceDeploymentConfig -Path $DeploymentConfigPath -Layout $layout -FallbackDirectory $PSScriptRoot'
        New = '        Remove-Item -LiteralPath $DeploymentConfigPath -Force -Recurse; $residue = $null'
    }
    'r5 installer deletes the previous generation with [IO.Directory]::Delete' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '    Remove-ParallelInstanceDirectory -Path $previousRoot'
        New = '    [IO.Directory]::Delete($previousRoot, $true)'
    }
    'r5 installer skips the config-path refusal' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '    if ($configRefusal) { throw "-DeploymentConfigPath $configRefusal." }'
        New = '    # (refusal removed)'
    }
    'r5 config path equality check removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '        return "is ''$Path''; the only accepted path is ''$($Layout.DeploymentConfigPath)''"'
        New = '        $null = 0'
    }
    'r5 config plain-file check removed' = @{
        File = 'ParallelInstance.psm1'
        Old = '    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return ''is not an existing file'' }'
        New = ''
    }
    'r5 result names predictable again' = @{
        File = 'ParallelHost.psm1'
        Old = '[guid]::NewGuid().ToString(''N''))'
        New = '''fixed'')'
    }
    'r5 map separators: dashes no longer folded' = @{
        File = 'ParallelInstance.psm1'
        Old = '    return [regex]::Replace($key, ''['
        New = '    return [regex]::Replace($key, ''[\s_]+'', ''-'') # ['
    }
    'r5 map token pattern back to (-|$)' = @{
        File = 'ParallelInstance.psm1'
        Old = '$script:ProductionMapTokenPattern = ''map-?25(?![0-9])'''
        New = '$script:ProductionMapTokenPattern = ''map-?25(-|$)'''
    }
    # ---- round 6: the secrets file's lifetime (every way out of the install) ----
    'r6 installer finally no longer cleans up' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '        $residue = Remove-ParallelInstanceDeploymentConfig -Path $DeploymentConfigPath -Layout $layout -FallbackDirectory $PSScriptRoot'
        New = '        $residue = $null'
    }
    'r6 residue reported silently' = @{
        File = 'Install-ParallelInstanceLocal.ps1'
        Old = '            Write-Warning $message'
        New = '            $null = $message'
    }
    'r6 without a layout nothing may be deleted' = @{
        File = 'ParallelInstance.psm1'
        Old = '            if (-not [string]::Equals($Path, $expected, [StringComparison]::OrdinalIgnoreCase)) {'
        New = '            if ($true) {'
    }
    'r6 without a layout anything may be deleted' = @{
        File = 'ParallelInstance.psm1'
        Old = '            if (-not [string]::Equals($Path, $expected, [StringComparison]::OrdinalIgnoreCase)) {'
        New = '            if ($false) {'
    }
    'r6 with a layout any path may be deleted' = @{
        File = 'ParallelInstance.psm1'
        Old = '            if (-not [string]::Equals($Path, $Layout.DeploymentConfigPath, [StringComparison]::OrdinalIgnoreCase)) {'
        New = '            if ($false) {'
    }
    'r6 config delete ignores links' = @{
        File = 'ParallelInstance.psm1'
        Old = "        if (`$isLink) { return 'it is a symbolic link or junction, not the file the control host copied' }"
        New = ''
    }
    # ---- round 7: one deletion scan for scripts and modules alike ----
    'r7 ParallelHost.psm1 deletes with ForEach-Object Delete' = @{
        File = 'ParallelHost.psm1'
        Old = '    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1'
        New = '    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1; if ($false) { Get-ChildItem -LiteralPath $ScriptRoot -Filter ''*.tmp'' | ForEach-Object Delete }'
    }
    'r7 the module deletes outside its two delete functions' = @{
        File = 'ParallelInstance.psm1'
        Old = '    $removed = [System.Collections.Generic.List[string]]::new()'
        New = '    $removed = [System.Collections.Generic.List[string]]::new(); if ($false) { Remove-Item -LiteralPath ''C:\nothing-cs262'' -Recurse }'
    }
    # ---- round 8: premise checked on the copy that runs; MAP25 ----
    'r8 uninstall-time premise check removed' = @{
        File = 'ParallelHost.psm1'
        Old = '    if ($broken.Count -gt 0) {'
        New = '    if ($false) {'
    }
    'r8 map token separator required again' = @{
        File = 'ParallelInstance.psm1'
        Old = '$script:ProductionMapTokenPattern = ''map-?25(?![0-9])'''
        New = '$script:ProductionMapTokenPattern = ''map-25(?![0-9])'''
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
