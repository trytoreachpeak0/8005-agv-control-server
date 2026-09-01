#requires -Version 7
<#
.SYNOPSIS
Ticket 09: rehearse the section 4.5 upgrade path on an isolated TLS-era instance.

.DESCRIPTION
Ticket 03 could only cover the configuration migration with a red/green pair, because
Update-ControlServerLocal.ps1 hard-coded the production service name and roots and so had exactly
one possible target. Three segments of the upgrade therefore had no execution evidence at all:
removal of the certificate directory, clearing of the machine-scope certificate password, and the
backup/rollback path. Ticket 09 parameterized the script; this run exercises all three on an
isolated instance built from the TLS-era candidate.

Two upgrades are run, in this order:

  1. against a TAMPERED package whose manifest is internally consistent but whose appsettings.json
     is not valid JSON. The failure lands after the backup, which is the only way to reach the
     rollback path, and the instance must come back on its original binaries.
  2. against the real new candidate, which must complete and report the three certificate segments.

The machine-scope variable is a probe name passed through -CertificatePasswordVariable. That is the
whole reason the parameter exists: with the production name the rehearsal would clear the running
production service's secret. The code path executed is identical; only the name differs.

Must run elevated.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260901b-19ce7db',
    [string]$LegacyReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260831-31263b1',
    [string]$ServiceName = '8005 AGV ControlServer Ticket09 Legacy',
    [int]$OnboardPort = 58605,
    [int]$HealthPort = 58607,
    [string]$ProbePasswordVariable = 'CONTROL_SERVER_TICKET09_PROBE_CERT_PASSWORD'
)

$ErrorActionPreference = 'Stop'

$assertions = [System.Collections.Generic.List[object]]::new()
function Add-Assertion {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Subject,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INCONCLUSIVE')][string]$Verdict,
        [string]$Control = $null,
        [string]$Note = $null
    )
    $assertions.Add([ordered]@{
            id = $Id; subject = $Subject; expected = $Expected; actual = $Actual
            verdict = $Verdict; control = $Control; note = $Note
        })
    '[{0,-12}] {1}' -f $Verdict, $Id | Write-Host
}

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Invoke-Ticket09UpgradeRehearsal.ps1 must run elevated.'
    }
}

# Same module the acceptance run and the red side use.
Import-Module (Join-Path $PSScriptRoot 'Ticket09Detectors.psm1') -Force

Assert-Administrator
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

$installRoot = Join-Path $RunRoot 'install'
$dataRoot = Join-Path $RunRoot 'data'
$backupRoot = Join-Path $RunRoot 'backups'
$evidence = Join-Path $RunRoot 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

$productionPasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$productionPasswordBefore = [Environment]::GetEnvironmentVariable($productionPasswordVariable, 'Machine')
$storeDigestsBefore = Get-AllStoreDigests
$productionBefore = Get-ProductionSnapshot
$certificateDirectory = Join-Path $dataRoot 'certs'
$installedConfiguration = Join-Path $installRoot 'appsettings.Production.json'

$serviceInstalled = $false
$probeSet = $false

try {
    # ============================================== A. an isolated TLS-era installation to upgrade
    $legacyInstallResult = Join-Path $evidence 'legacy-install-result.json'
    & (Join-Path $LegacyReleaseRoot 'scripts\Install-ControlServerLocal.ps1') `
        -PackagePath (Join-Path $LegacyReleaseRoot 'controlserver') `
        -ResultPath $legacyInstallResult -DiagnosticPath (Join-Path $evidence 'legacy-install-diagnostic.log') `
        -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot -BackupRoot $backupRoot `
        -OnboardPort $OnboardPort -HealthPort $HealthPort `
        -SkipMachineEnvironmentInjection *>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'legacy-install-console.log')
    $serviceInstalled = $true

    $legacyInstall = Get-Content -Raw -LiteralPath $legacyInstallResult | ConvertFrom-Json
    Copy-Item -LiteralPath $installedConfiguration -Destination (Join-Path $evidence 'legacy-appsettings.Production.json') -Force
    $legacyRemovedKeysPresent = @(Get-RemovedCertificateKeys -ConfigurationPath $installedConfiguration)
    $legacyCertFiles = @(@(Get-ChildItem -LiteralPath $certificateDirectory -File -ErrorAction SilentlyContinue).Name)

    Add-Assertion -Id 'LEGACY-INSTALL' -Subject 'a TLS-era instance is installed as the thing to be upgraded' `
        -Expected 'install PASS, certs directory populated, all four soon-to-be-removed keys present in its configuration' `
        -Actual ("result={0} sourceCommit={1} certFiles={2} keysPresent={3}" -f
            $legacyInstall.result, $legacyInstall.sourceCommit, ($legacyCertFiles -join ','), ($legacyRemovedKeysPresent -join ',')) `
        -Verdict $(if ($legacyInstall.result -eq 'PASS' -and $legacyCertFiles.Count -gt 0 -and
                       $legacyRemovedKeysPresent.Count -eq 4) { 'PASS' } else { 'FAIL' }) `
        -Control 'the acceptance run applied the same four-key detector to the new installer output and found 0 of them' `
        -Note 'This is the shape a real site is upgrading from, not a hand-written approximation of it.'

    # A probe value in a probe variable. With the production variable name this rehearsal would
    # clear the secret the running production service depends on.
    $probeValue = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
    [Environment]::SetEnvironmentVariable($ProbePasswordVariable, $probeValue, 'Machine')
    $probeSet = $true
    $probeReadBack = [Environment]::GetEnvironmentVariable($ProbePasswordVariable, 'Machine')
    Add-Assertion -Id 'PROBE-VARIABLE-SET' -Subject 'the machine-scope variable the upgrade is asked to clear exists before the upgrade' `
        -Expected "$ProbePasswordVariable present with the value this run generated" `
        -Actual "present=$([bool]$probeReadBack) matchesGenerated=$($probeReadBack -ceq $probeValue)" `
        -Verdict $(if ($probeReadBack -ceq $probeValue) { 'PASS' } else { 'FAIL' }) `
        -Note 'Value never printed. Without this precondition the clearing segment would trivially "pass" by having nothing to do.'

    $installBefore = Get-TreeHashes $installRoot
    $configurationHashBefore = (Get-FileHash -LiteralPath $installedConfiguration -Algorithm SHA256).Hash.ToLowerInvariant()

    # =========================================================== B. failing upgrade and rollback
    $tamperedPackage = Join-Path $RunRoot 'tampered-package'
    Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'controlserver') -Destination $tamperedPackage -Recurse
    $tamperedSettings = Join-Path $tamperedPackage 'appsettings.json'
    [IO.File]::WriteAllText($tamperedSettings, '{ this is not valid json', [Text.UTF8Encoding]::new($false))
    $tamperedManifestPath = Join-Path $tamperedPackage 'deployment-manifest.json'
    $tamperedManifest = Get-Content -Raw -LiteralPath $tamperedManifestPath | ConvertFrom-Json
    foreach ($entry in $tamperedManifest.files) {
        if ($entry.path -eq 'appsettings.json') {
            $entry.sha256 = (Get-FileHash -LiteralPath $tamperedSettings -Algorithm SHA256).Hash.ToLowerInvariant()
            $entry.length = (Get-Item -LiteralPath $tamperedSettings).Length
        }
    }
    [IO.File]::WriteAllText($tamperedManifestPath, ($tamperedManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $rollbackError = $null
    try {
        & (Join-Path $ReleaseRoot 'scripts\Update-ControlServerLocal.ps1') `
            -PackagePath $tamperedPackage `
            -ResultPath (Join-Path $evidence 'rollback-result.json') `
            -DiagnosticPath (Join-Path $evidence 'rollback-diagnostic.log') `
            -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot -BackupRoot $backupRoot `
            -CertificatePasswordVariable $ProbePasswordVariable *>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'rollback-console.log')
    }
    catch { $rollbackError = $_ }

    $rollbackDiagnostic = @(Get-Content -LiteralPath (Join-Path $evidence 'rollback-diagnostic.log') -ErrorAction SilentlyContinue |
        ForEach-Object { ($_ -split ' ', 2)[1] })
    $installAfterRollback = Get-TreeHashes $installRoot
    $installDifferences = Compare-TreeHashes $installBefore $installAfterRollback
    $serviceAfterRollback = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $probeAfterRollback = [Environment]::GetEnvironmentVariable($ProbePasswordVariable, 'Machine')

    Add-Assertion -Id 'UPGRADE-FAILS-ON-BAD-PACKAGE' -Subject 'a package that passes the manifest check but cannot start is not silently accepted' `
        -Expected 'the upgrade throws, and the diagnostic log shows it got past backup-complete before failing' `
        -Actual ("threw={0} steps={1}" -f [bool]$rollbackError, ($rollbackDiagnostic -join '>')) `
        -Verdict $(if ($rollbackError -and $rollbackDiagnostic -contains 'backup-complete' -and
                       @($rollbackDiagnostic | Where-Object { $_ -like 'failure:*' }).Count -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Note 'Failing before the backup would prove nothing about rollback; the manifest hashes were regenerated so the tamper survives preflight.'

    Add-Assertion -Id 'UPGRADE-ROLLBACK-RESTORES-INSTALL' -Subject 'rollback puts the original installation back byte for byte' `
        -Expected '0 differences between the install tree before the upgrade and after the rollback' `
        -Actual ("differences={0} {1}" -f $installDifferences.Count, (($installDifferences | Select-Object -First 5) -join ',')) `
        -Verdict $(if ($installDifferences.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same comparer reports the differences introduced by the successful upgrade later in this run (must be non-zero there)"

    Add-Assertion -Id 'UPGRADE-ROLLBACK-RESTORES-SECRET' -Subject 'rollback restores the machine-scope certificate password it cleared' `
        -Expected 'the probe variable holds the same value it held before the failed upgrade' `
        -Actual "present=$([bool]$probeAfterRollback) unchanged=$($probeAfterRollback -ceq $probeValue)" `
        -Verdict $(if ($probeAfterRollback -ceq $probeValue) { 'PASS' } else { 'FAIL' }) `
        -Note 'This is the segment ticket 03 could not execute at all.'

    Add-Assertion -Id 'UPGRADE-ROLLBACK-RESTORES-SERVICE' -Subject 'the instance is running again on its original binaries after rollback' `
        -Expected 'service Running and the certificate directory restored from the backup' `
        -Actual ("status={0} certsDirectory={1} certFiles={2}" -f
            $(if ($serviceAfterRollback) { $serviceAfterRollback.Status } else { '<absent>' }),
            (Test-Path -LiteralPath $certificateDirectory),
            @(@(Get-ChildItem -LiteralPath $certificateDirectory -File -ErrorAction SilentlyContinue).Name).Count) `
        -Verdict $(if ($serviceAfterRollback -and $serviceAfterRollback.Status -eq 'Running' -and
                       (Test-Path -LiteralPath $certificateDirectory)) { 'PASS' } else { 'FAIL' })

    # ================================================================= C. the successful upgrade
    $upgradeResultPath = Join-Path $evidence 'upgrade-result.json'
    & (Join-Path $ReleaseRoot 'scripts\Update-ControlServerLocal.ps1') `
        -PackagePath (Join-Path $ReleaseRoot 'controlserver') `
        -ResultPath $upgradeResultPath `
        -DiagnosticPath (Join-Path $evidence 'upgrade-diagnostic.log') `
        -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot -BackupRoot $backupRoot `
        -CertificatePasswordVariable $ProbePasswordVariable *>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'upgrade-console.log')

    $upgrade = Get-Content -Raw -LiteralPath $upgradeResultPath | ConvertFrom-Json
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Add-Assertion -Id 'UPGRADE-PASS' -Subject 'the documented section 4.5 upgrade completes on a real TLS-era installation' `
        -Expected 'result PASS, service Running, sourceCommit equal to the new candidate' `
        -Actual "result=$($upgrade.result) status=$(if ($service) { $service.Status } else { '<absent>' }) sourceCommit=$($upgrade.sourceCommit) transport=$($upgrade.transport)" `
        -Verdict $(if ($upgrade.result -eq 'PASS' -and $service -and $service.Status -eq 'Running') { 'PASS' } else { 'FAIL' })

    $certificateDirectoryGone = -not (Test-Path -LiteralPath $certificateDirectory)
    Add-Assertion -Id 'UPGRADE-CERT-DIRECTORY-REMOVED' -Subject 'the upgrade removes the certificate directory from the data root' `
        -Expected "reported removed AND $certificateDirectory absent on readback" `
        -Actual ("reported={0} presentOnReadback={1}" -f
            $upgrade.certificateRemoval.certificateDirectoryRemoved, (Test-Path -LiteralPath $certificateDirectory)) `
        -Verdict $(if ($upgrade.certificateRemoval.certificateDirectoryRemoved -and $certificateDirectoryGone) { 'PASS' } else { 'FAIL' }) `
        -Control "LEGACY-INSTALL recorded $($legacyCertFiles.Count) files in that directory before the upgrade" `
        -Note 'Ticket 13 note applied: the reported field is the script talking about itself, so the readback is the evidence.'

    $probeAfterUpgrade = [Environment]::GetEnvironmentVariable($ProbePasswordVariable, 'Machine')
    $productionPasswordAfterUpgrade = [Environment]::GetEnvironmentVariable($productionPasswordVariable, 'Machine')
    Add-Assertion -Id 'UPGRADE-MACHINE-PASSWORD-CLEARED' -Subject 'the upgrade clears the machine-scope certificate password it was pointed at, and only that one' `
        -Expected 'the probe variable is gone and the production variable still holds its original value' `
        -Actual ("probePresent={0} productionUnchanged={1}" -f
            [bool]$probeAfterUpgrade, ($productionPasswordBefore -ceq $productionPasswordAfterUpgrade)) `
        -Verdict $(if (-not $probeAfterUpgrade -and ($productionPasswordBefore -ceq $productionPasswordAfterUpgrade)) { 'PASS' } else { 'FAIL' }) `
        -Control "PROBE-VARIABLE-SET recorded the same variable present with a known value minutes earlier in this run" `
        -Note 'Both halves matter: clearing everything named like a certificate password would also satisfy the first half alone.'

    Copy-Item -LiteralPath $installedConfiguration -Destination (Join-Path $evidence 'migrated-appsettings.Production.json') -Force
    $migrated = Get-Content -Raw -LiteralPath $installedConfiguration | ConvertFrom-Json
    $migratedKeysPresent = @(Get-RemovedCertificateKeys -ConfigurationPath $installedConfiguration)
    Add-Assertion -Id 'UPGRADE-CONFIG-MIGRATED' -Subject 'the retained production configuration is migrated to the plaintext key set' `
        -Expected 'all four removed keys gone, Health:url rewritten to http' `
        -Actual ("keysStillPresent={0} reportedRemoved={1} healthUrl={2} rewritten={3}" -f
            ($migratedKeysPresent -join ','), (@($upgrade.certificateRemoval.removedConfigurationKeys) -join ','),
            $migrated.Health.url, $upgrade.certificateRemoval.healthUrlRewrittenToHttp) `
        -Verdict $(if ($migratedKeysPresent.Count -eq 0 -and $migrated.Health.url -like 'http://*') { 'PASS' } else { 'FAIL' }) `
        -Control "LEGACY-INSTALL found all four of those key names in this same file before the upgrade"

    $installAfterUpgrade = Get-TreeHashes $installRoot
    $upgradeDifferences = Compare-TreeHashes $installBefore $installAfterUpgrade
    Add-Assertion -Id 'UPGRADE-REPLACED-BINARIES' -Subject 'the upgrade actually replaced the installation' `
        -Expected 'the same comparer that reported 0 differences after rollback reports differences here' `
        -Actual "differences=$($upgradeDifferences.Count)" `
        -Verdict $(if ($upgradeDifferences.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Note 'This is the control for UPGRADE-ROLLBACK-RESTORES-INSTALL: the comparer is shown to be capable of reporting a difference.'

    $healthUri = $migrated.Health.url.TrimEnd('/')
    $liveBody = (& "$env:SystemRoot\System32\curl.exe" --noproxy '*' -s -w "`nHTTP_STATUS=%{http_code}" --max-time 20 "$healthUri/health/live" 2>&1) -join "`n"
    Add-Assertion -Id 'UPGRADE-LIVE-OVER-HTTP' -Subject 'the upgraded instance answers /health/live over plain HTTP' `
        -Expected '200 with status live at the migrated Health:url' -Actual ($liveBody -replace "`n", ' ') `
        -Verdict $(if ($liveBody -match 'HTTP_STATUS=200' -and $liveBody -match '"status"\s*:\s*"live"') { 'PASS' } else { 'FAIL' }) `
        -Control 'the same endpoint on the same port spoke HTTPS before the upgrade; the legacy install verified it with curl --cacert'

    $keyMaterial = @(Get-KeyMaterialFiles -Path @($installRoot, $dataRoot))
    $backupKeyMaterial = @(Get-KeyMaterialFiles -Path @($backupRoot))
    Add-Assertion -Id 'UPGRADE-NO-KEY-MATERIAL-LEFT' -Subject 'no key material survives under the upgraded install and data roots' `
        -Expected '0 key-material files' -Actual "found=$($keyMaterial.Count) $($keyMaterial -join ';')" `
        -Verdict $(if ($keyMaterial.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same detector over the backup root still finds the legacy material the rollback path depends on: $($backupKeyMaterial.Count) (must be non-zero)" `
        -Note 'The backups are inside the disposable run root and go away with it; a production upgrade leaves them under the backup root on purpose.'

    Add-Assertion -Id 'UPGRADE-CURRENTUSER-ROOT-IS-MANUAL' -Subject 'the upgrade does not claim to have removed the CurrentUser\Root certificate' `
        -Expected 'currentUserRootCertificateRemoved false and currentUserRootCertificateRemovalIsManual true' `
        -Actual ("removed={0} manual={1}" -f $upgrade.certificateRemoval.currentUserRootCertificateRemoved,
            $upgrade.certificateRemoval.currentUserRootCertificateRemovalIsManual) `
        -Verdict $(if ($upgrade.certificateRemoval.currentUserRootCertificateRemoved -eq $false -and
                       $upgrade.certificateRemoval.currentUserRootCertificateRemovalIsManual -eq $true) { 'PASS' } else { 'FAIL' }) `
        -Note 'Manual section 4.5 carries the manual removal; reporting it as cleaned here would be a false green.'
}
catch {
    Add-Assertion -Id 'REHEARSAL-ABORTED' -Subject 'the upgrade rehearsal completed without an unhandled failure' `
        -Expected 'no unhandled exception' -Actual $_.Exception.Message -Verdict 'FAIL'
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'run-exception.txt') -Encoding utf8NoBOM
}
finally {
    if ($serviceInstalled) {
        try {
            & (Join-Path $ReleaseRoot 'scripts\Uninstall-ControlServerLocal.ps1') `
                -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot `
                -ResultPath (Join-Path $evidence 'legacy-uninstall-result.json') -RemoveDataRoot -ConfirmUninstall *>&1 |
                Tee-Object -FilePath (Join-Path $evidence 'legacy-uninstall-console.log')
            $gone = $null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
            Add-Assertion -Id 'REHEARSAL-UNINSTALL' -Subject 'the rehearsal instance is removed' `
                -Expected 'service absent, install root and data root removed' `
                -Actual "serviceAbsent=$gone installRoot=$(Test-Path -LiteralPath $installRoot) dataRoot=$(Test-Path -LiteralPath $dataRoot)" `
                -Verdict $(if ($gone -and -not (Test-Path -LiteralPath $installRoot) -and -not (Test-Path -LiteralPath $dataRoot)) { 'PASS' } else { 'FAIL' })
        }
        catch {
            Add-Assertion -Id 'REHEARSAL-UNINSTALL' -Subject 'the rehearsal instance is removed' `
                -Expected 'service absent' -Actual $_.Exception.Message -Verdict 'FAIL'
        }
    }
    if ($probeSet) { [Environment]::SetEnvironmentVariable($ProbePasswordVariable, $null, 'Machine') }
    $probeFinal = [Environment]::GetEnvironmentVariable($ProbePasswordVariable, 'Machine')
    Add-Assertion -Id 'REHEARSAL-PROBE-VARIABLE-GONE' -Subject 'the probe machine-scope variable does not outlive the rehearsal' `
        -Expected "$ProbePasswordVariable absent" -Actual "present=$([bool]$probeFinal)" `
        -Verdict $(if (-not $probeFinal) { 'PASS' } else { 'FAIL' })

    $storeDigestsFinal = Get-AllStoreDigests
    $storeChanges = Compare-StoreDigests $storeDigestsBefore $storeDigestsFinal
    Add-Assertion -Id 'REHEARSAL-CERT-STORES-UNCHANGED' -Subject 'the whole rehearsal leaves the certificate stores as it found them' `
        -Expected '0 of the 4 observed stores changes its sorted-thumbprint digest' `
        -Actual "changed=$($storeChanges.Count) $($storeChanges -join '; ')" `
        -Verdict $(if ($storeChanges.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Note 'The legacy installer creates its root and leaf in CurrentUser\My and deletes both after export; -InstallCurrentUserRoot was deliberately not passed.'

    $productionFinal = Get-ProductionSnapshot
    $productionDrift = @(Compare-ProductionSnapshot $productionBefore $productionFinal)
    $productionPasswordFinal = [Environment]::GetEnvironmentVariable($productionPasswordVariable, 'Machine')
    $productionPasswordUnchanged = ($productionPasswordBefore -ceq $productionPasswordFinal)
    Add-Assertion -Id 'REHEARSAL-PRODUCTION-UNAFFECTED' -Subject 'the production service and its machine-scope secret survive the rehearsal' `
        -Expected "no drift against the pre-run snapshot (PID $($productionBefore.processIds -join ','), $($productionBefore.listeners -join ' ')) and the certificate password unchanged" `
        -Actual ("drift={0} {1}; status={2} passwordUnchanged={3}" -f
            $productionDrift.Count, ($productionDrift -join '; '), $productionFinal.status, $productionPasswordUnchanged) `
        -Verdict $(if ($productionDrift.Count -eq 0 -and $productionFinal.status -eq 'Running' -and
                       $productionPasswordUnchanged) { 'PASS' } else { 'FAIL' }) `
        -Note 'The entire point of -CertificatePasswordVariable: this assertion would fail if the rehearsal had used the production name.'

    $summary = [ordered]@{
        schemaVersion = 1
        runKind = 'TICKET09_UPGRADE_PATH_REHEARSAL_ISOLATED'
        releaseRoot = $ReleaseRoot
        legacyReleaseRoot = $LegacyReleaseRoot
        serviceName = $ServiceName
        probePasswordVariable = $ProbePasswordVariable
        productionTargeted = $false
        storeDigestsBefore = $storeDigestsBefore
        storeDigestsFinal = $storeDigestsFinal
        productionBefore = $productionBefore
        productionFinal = $productionFinal
        finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
        counts = [ordered]@{
            pass = @($assertions | Where-Object verdict -eq 'PASS').Count
            fail = @($assertions | Where-Object verdict -eq 'FAIL').Count
            inconclusive = @($assertions | Where-Object verdict -eq 'INCONCLUSIVE').Count
        }
        assertions = $assertions
    }
    [IO.File]::WriteAllText((Join-Path $evidence 'upgrade-assertions.json'),
        ($summary | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    'PASS={0} FAIL={1} INCONCLUSIVE={2}' -f $summary.counts.pass, $summary.counts.fail, $summary.counts.inconclusive
}
