#Requires -Version 7
# control-server#466 mutations. Each one copies scripts/ and the slice index into a throwaway directory, commits it
# there as a git repository (the runners now read their own identity from the repository they live in, so a plain
# copy would turn section 2 red for every mutation alike), changes one thing, and runs Test-G3EvidenceHonesty.ps1.
# A move mutation (Anchor) deletes From and inserts it after Anchor instead of replacing it.
param([string]$Repo, [string]$OutDir)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$provenanceStatement = "`$runnerProvenance = Get-G3RunnerProvenance -ScriptRoot `$PSScriptRoot -Inputs ([ordered]@{`n        ControlServerRepository = @{ Given = `$ControlServerRepository; Default = (Split-Path -Parent `$PSScriptRoot) }`n    })`n"
$mutations = @(
    @{ Id = 'M0-baseline'; File = 'g3-slice-evidence.ps1'; From = $null; To = $null },
    # The ticket's three, at the point each is decided.
    @{ Id = 'M1-dirty-worktree-counts-as-clean'; File = 'g3-slice-evidence.ps1'
       From = '        $clean = $status.Count -eq 0 -and $hidden.Count -eq 0'
       To = '        $clean = $true' },
    @{ Id = 'M2-assume-unchanged-and-skip-worktree-not-seen'; File = 'g3-slice-evidence.ps1'
       From = '        $clean = $status.Count -eq 0 -and $hidden.Count -eq 0'
       To = '        $clean = $status.Count -eq 0' },
    @{ Id = 'M3-input-override-ignored'; File = 'g3-slice-evidence.ps1'
       From = "    if (`$overridden.Count -ne 0) {`n        `$reasons.Insert("
       To = "    if (`$false) {`n        `$reasons.Insert(" },
    @{ Id = 'M4-staged-binding-read-from-disk'; File = 'run-staged-g3.ps1'
       From = '-Binding ($runnerProvenance.bindingAtHead ?? (Get-SharedCommitBinding -Path (Join-Path $PSScriptRoot ''run-staged-g3.ps1'')))'
       To = '-Binding (Get-SharedCommitBinding -Path (Join-Path $PSScriptRoot ''run-staged-g3.ps1''))' },
    @{ Id = 'M5-journey-binding-read-off-shared-runner-source'; File = 'run-journey-g3.ps1'
       From = '-Binding ($runnerProvenance.bindingAtHead ?? $commitBinding)'
       To = '-Binding $commitBinding' },
    @{ Id = 'M6-demand-bearing-binding-read-off-shared-runner-source'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = '-Binding ($runnerProvenance.bindingAtHead ?? $commitBinding)'
       To = '-Binding $commitBinding' },
    @{ Id = 'M7-restart-binding-read-from-disk'; File = 'run-staged-g3-restart.ps1'
       From = '-Binding ($runnerProvenance.bindingAtHead ?? $commitBinding)'
       To = '-Binding $commitBinding' },
    @{ Id = 'M8-demand-bearing-shared-runner-source-not-an-input'; File = 'run-demand-bearing-g3-vectors.ps1'
       From = "        SharedRunnerSource = @{ Given = `$SharedRunnerSource; Default = (Join-Path `$PSScriptRoot 'run-staged-g3.ps1') }`n"
       To = '' },
    @{ Id = 'M9-journey-binding-reader-source-not-an-input'; File = 'run-journey-g3.ps1'
       From = "        CommitBindingFunctionSource = @{ Given = `$CommitBindingFunctionSource; Default = (Join-Path `$PSScriptRoot 'run-staged-g3-restart.ps1') }`n"
       To = '' },
    # Grading.
    @{ Id = 'M10-runner-source-not-graded'; File = 'g3-slice-evidence.ps1'
       From = "    if ('runnerSource' -notin `$keys) {"
       To = "    if (`$false) {" },
    @{ Id = 'M11-dirty-runner-source-passes'; File = 'g3-slice-evidence.ps1'
       From = "    } elseif (`"`$(`$Commits.runnerSource)`" -cne 'COMMITTED_RUNNER') {"
       To = "    } elseif (`$false) {" },
    @{ Id = 'M12-unrecognised-runner-source-passes'; File = 'g3-slice-evidence.ps1'
       From = '                $reasons.Add("UNRECOGNISED_RUNNER_SOURCE: $token") }'
       To = '                }' },
    @{ Id = 'M13-unknown-provenance-passes'; File = 'g3-slice-evidence.ps1'
       From = '        $reasons.Add("RUNNER_PROVENANCE_UNKNOWN: $($_.Exception.Message.Replace('';'', '','').Trim())")'
       To = '        $null = 0' },
    # Wiring.
    @{ Id = 'M14-staged-record-runner-source-constant'; File = 'run-staged-g3.ps1'
       From = '    runnerSource = $runnerProvenance.runnerSource'
       To = '    runnerSource = ''COMMITTED_RUNNER''' },
    @{ Id = 'M15-journey-measures-control-server-repository'; File = 'run-journey-g3.ps1'
       From = '$runnerProvenance = Get-G3RunnerProvenance -ScriptRoot $PSScriptRoot'
       To = '$runnerProvenance = Get-G3RunnerProvenance -ScriptRoot $ControlServerRepository' },
    @{ Id = 'M16-restart-measures-after-evidence-root-is-created'; File = 'run-staged-g3-restart.ps1'
       From = $provenanceStatement
       To = $provenanceStatement
       Anchor = "New-Item -ItemType Directory -Path `$StageRoot, `$EvidenceRoot | Out-Null`n" }
)

foreach ($m in $mutations) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("cs466-mut-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
    New-Item -ItemType Directory -Path (Join-Path $root 'vendor\8005-agv-protocol\integration-slices') | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repo 'scripts') -Destination (Join-Path $root 'scripts') -Recurse
    Copy-Item -LiteralPath (Join-Path $Repo 'vendor\8005-agv-protocol\integration-slices\index.json') `
        -Destination (Join-Path $root 'vendor\8005-agv-protocol\integration-slices\index.json')
    if ($null -ne $m.From) {
        $path = Join-Path $root "scripts\$($m.File)"
        $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        $hits = ([regex]::Matches($text, [regex]::Escape($m.From))).Count
        if ($hits -ne 1) { throw "$($m.Id): expected one match, found $hits" }
        if ($null -ne $m.Anchor) {
            $text = $text.Replace($m.From, '')
            if (([regex]::Matches($text, [regex]::Escape($m.Anchor))).Count -ne 1) { throw "$($m.Id): expected one anchor" }
            $text = $text.Replace($m.Anchor, $m.Anchor + $m.To)
        } else {
            $text = $text.Replace($m.From, $m.To)
        }
        [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    }
    & git -C $root init --quiet 2>&1 | Out-Null
    & git -C $root add --all 2>&1 | Out-Null
    & git -C $root -c user.name=mutation -c user.email=mutation@invalid -c commit.gpgsign=false commit --quiet -m $m.Id 2>&1 | Out-Null
    $out = & pwsh -NoProfile -File (Join-Path $root 'scripts\Test-G3EvidenceHonesty.ps1') *>&1 | Out-String
    $code = $LASTEXITCODE
    $fails = @($out -split "`r?`n" | Where-Object { $_ -like 'FAIL *' })
    $last = @($out -split "`r?`n" | Where-Object { $_ -like '*self-check:*' })
    "== $($m.Id) ($($m.File)) exit $code, $($fails.Count) FAIL`n" + (($fails | Select-Object -First 4) -join "`n") + "`n" + ($last -join "`n")
    Set-Content -LiteralPath (Join-Path $OutDir "$($m.Id).txt") -Value $out
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
