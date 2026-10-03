#Requires -Version 7
param([string]$Repo, [string]$OutDir)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$mutations = @(
    @{ Id = 'M1-override-not-withheld'; File = 'g3-slice-evidence.ps1'
       From = "            (`$claim.assuranceLevel -in (Get-G3AssuranceLevelsThatCountAsSlicePass)) -and`n            (`$null -eq `$withheld)"
       To = "            (`$claim.assuranceLevel -in (Get-G3AssuranceLevelsThatCountAsSlicePass))" },
    @{ Id = 'M2-journey-record-drops-onboard-source'; File = 'run-journey-g3.ps1'
       From = "    onboardCommitSource = `$onboardCommitSource`n    slotsSimulator"
       To = "    slotsSimulator" },
    @{ Id = 'M3-no-runner-error-before-gate-results'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = "Write-StagedRunError -ErrorRecord `$runError -EvidenceRoot `$EvidenceRoot -RunLabel 'Demand-bearing G3'"
       To = "# removed" },
    @{ Id = 'M4-field-run-guard-call-removed'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = "    Assert-FieldRunStoreIsNotGenerated -StoreSource `$storeSource -Baseline `$baseline`n"
       To = "" },
    @{ Id = 'M5-field-run-guard-never-throws'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = "    if (`$StoreSource -ne 'FIELD_RUN') { return }"
       To = "    return" },
    @{ Id = 'M6-vehicle-key-marker-dropped'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = "        if (`"`$(`$row['vehicleKey'])`" -like 'BROKERX-L2-*') {"
       To = "        if (`$false) {" }
)

foreach ($m in $mutations) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("cs460-mut-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
    New-Item -ItemType Directory -Path (Join-Path $root 'vendor\8005-agv-protocol\integration-slices') | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repo 'scripts') -Destination (Join-Path $root 'scripts') -Recurse
    Copy-Item -LiteralPath (Join-Path $Repo 'vendor\8005-agv-protocol\integration-slices\index.json') `
        -Destination (Join-Path $root 'vendor\8005-agv-protocol\integration-slices\index.json')
    $path = Join-Path $root "scripts\$($m.File)"
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $hits = ([regex]::Matches($text, [regex]::Escape($m.From))).Count
    if ($hits -ne 1) { throw "$($m.Id): expected one match, found $hits" }
    [IO.File]::WriteAllText($path, $text.Replace($m.From, $m.To), [Text.UTF8Encoding]::new($false))
    $out = & pwsh -NoProfile -File (Join-Path $root 'scripts\Test-G3EvidenceHonesty.ps1') *>&1 | Out-String
    $code = $LASTEXITCODE
    $fails = @($out -split "`r?`n" | Where-Object { $_ -like 'FAIL *' -or $_ -like '*self-check:*' })
    $summary = "== $($m.Id) ($($m.File)) exit $code`n" + ($fails -join "`n")
    Set-Content -LiteralPath (Join-Path $OutDir "$($m.Id).txt") -Value $out
    $summary
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
