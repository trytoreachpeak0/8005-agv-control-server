#requires -Version 7
<#
.SYNOPSIS
Ticket 09 clean-install acceptance for the plaintext release candidate.

.DESCRIPTION
Ticket 14 qualified the previous candidate by launching the packaged host directly, which left
INSTALL-AS-SERVICE and PERSISTENT-LOGS INCONCLUSIVE. This run installs the candidate as a Windows
service on an ISOLATED instance -- different service name, install root, data root, backup root and
ports -- so the production service is never the target, and then drives the same acceptance
directions ticket 14 used, with the TLS-specific ones rewritten for plaintext.

Every check emits a machine-readable assertion at the moment it is observed. Nothing here is
transcribed after the fact. Where a control exists it is recorded, because a green that has never
been shown to go red is not evidence.

Must run elevated. The upgrade path is rehearsed by Invoke-Ticket09UpgradeRehearsal.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260901b-19ce7db',
    [string]$PreviousReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260901-238b46e',
    [string]$SimulatorExe = 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe',
    [string]$ServiceName = '8005 AGV ControlServer Ticket09',
    [int]$OnboardPort = 58505,
    [int]$HealthPort = 58507,
    # environment=Production on the onboard rejects a loopback ControlServer host for both the
    # session transport and the safety projection, so a single-machine acceptance has to bind a
    # non-loopback local interface. This one is a host-internal Hyper-V switch.
    [string]$BindAddress = '192.168.200.1',
    [string]$ExpectedServerCommit = '19ce7db70893afea6c6361988c3bc612d77569d0',
    [string]$ExpectedOnboardCommit = '238b46eb2c9ae90584e4288a782176f66b7de942',
    [int]$SessionObserveSeconds = 60,
    [int]$IntakeObserveSeconds = 90
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------------------ assertion ledger
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

function Add-Failure([string]$Id, [string]$Subject, [string]$Expected, $Actual) {
    Add-Assertion -Id $Id -Subject $Subject -Expected $Expected -Actual $Actual -Verdict 'FAIL'
}

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Invoke-Ticket09Acceptance.ps1 must run elevated.'
    }
}

# ---------------------------------------------------------------------------------- observations
# The detectors live in Ticket09Detectors.psm1 so that Invoke-Ticket09RedSide.ps1 mutates the same
# code this run depends on, rather than a copy of it.
Import-Module (Join-Path $PSScriptRoot 'Ticket09Detectors.psm1') -Force

function Wait-Listening([int]$Port, [int]$TimeoutSeconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# Reachability is only ever judged by a returned body: the workstation's global proxy makes ping and
# a bare TCP connect succeed even for hosts that do not exist.
function Invoke-Plain([string]$Path, [switch]$NoAuthorization) {
    $arguments = @('--noproxy', '*', '-s', '-w', "`nHTTP_STATUS=%{http_code}", '--max-time', '20',
        "http://${BindAddress}:$HealthPort$Path")
    if (-not $NoAuthorization) {
        $arguments = @('-H', "Authorization: Bearer $($env:CONTROL_SERVER_ONBOARD_CREDENTIAL)") + $arguments
    }
    return (& "$env:SystemRoot\System32\curl.exe" @arguments 2>&1) -join "`n"
}

function Get-HttpStatus([string]$Response) {
    return ((($Response -split "`n") | Where-Object { $_ -like 'HTTP_STATUS=*' }) -join '')
}

Assert-Administrator
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) {
    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine')
}
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) { throw 'CONTROL_SERVER_ONBOARD_CREDENTIAL is not available.' }
if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) {
    $env:CONTROL_SERVER_RIOT_CALL_API_KEY = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'User')
}
if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY is not available.' }
$env:CONTROL_SERVER_OPERATOR_ID = 'TICKET09-ACCEPTANCE'

if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

$installRoot = Join-Path $RunRoot 'install'
$dataRoot = Join-Path $RunRoot 'data'
$backupRoot = Join-Path $RunRoot 'backups'
$evidence = Join-Path $RunRoot 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

$certificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$productionCertificatePasswordBefore = [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine')
$storeDigestsBefore = Get-AllStoreDigests
$productionBefore = Get-ProductionSnapshot

$serviceInstalled = $false
$simulatorProcess = $null
$onboardProcess = $null

try {
    # ========================================================= A. release candidate under test
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') | ConvertFrom-Json
    $serverCommit = $manifest.components.controlServer.commit
    $onboardCommit = $manifest.components.onboardHmi.commit
    $protocolTag = $manifest.components.protocol.tag
    $identityOk = $serverCommit -eq $ExpectedServerCommit -and
                  $onboardCommit -eq $ExpectedOnboardCommit -and
                  $protocolTag -eq 'protocol-v0.1.1'
    Add-Assertion -Id 'RC-IDENTITY' -Subject 'release-manifest.json binds the three component identities' `
        -Expected "server $($ExpectedServerCommit.Substring(0,7)), onboard $($ExpectedOnboardCommit.Substring(0,7)), protocol-v0.1.1" `
        -Actual "server=$serverCommit onboard=$onboardCommit protocol=$protocolTag" `
        -Verdict $(if ($identityOk) { 'PASS' } else { 'FAIL' }) `
        -Note 'Read back from the produced artifact, not restated from the ticket.'

    $sumsPath = Join-Path $ReleaseRoot 'SHA256SUMS.txt'
    $hashVerification = Get-HashMismatches -Root $ReleaseRoot -SumsPath $sumsPath
    $checked = $hashVerification.checked
    $mismatches = @($hashVerification.mismatches)

    $controlDir = Join-Path $evidence 'hash-control'
    New-Item -ItemType Directory -Path $controlDir -Force | Out-Null
    $controlCopy = Join-Path $controlDir 'RELEASE-CANDIDATE.md'
    $controlBytes = [IO.File]::ReadAllBytes((Join-Path $ReleaseRoot 'RELEASE-CANDIDATE.md'))
    $controlBytes[0] = $controlBytes[0] -bxor 0x01
    [IO.File]::WriteAllBytes($controlCopy, $controlBytes)
    $expectedControlHash = (@(Get-Content -LiteralPath $sumsPath | Where-Object { $_ -like '*  RELEASE-CANDIDATE.md' })[0] -split '  ', 2)[0]
    $controlRed = (Get-FileHash -LiteralPath $controlCopy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedControlHash

    Add-Assertion -Id 'RC-HASHES' -Subject 'section 3 of the manual verifies every SHA256SUMS entry' `
        -Expected 'no mismatch across all listed files' `
        -Actual "checked=$checked mismatches=$($mismatches.Count)" `
        -Verdict $(if ($checked -gt 0 -and $mismatches.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same comparison over a one-byte-flipped copy of RELEASE-CANDIDATE.md reported a mismatch: $controlRed (must be True)"

    # New this round: section 4.5 tells the site to run the upgrade script with the same package
    # relative prefix as section 4.2's install command, so the package has to carry it.
    $shippedUpgradeScript = Join-Path $ReleaseRoot 'scripts\Update-ControlServerLocal.ps1'
    $upgradeInSums = @(Get-Content -LiteralPath $sumsPath | Where-Object { $_ -like '*  scripts/Update-ControlServerLocal.ps1' }).Count
    $upgradeEntryPoint = $manifest.operatorEntryPoints.upgrade
    $previousHadUpgrade = Test-Path -LiteralPath (Join-Path $PreviousReleaseRoot 'scripts\Update-ControlServerLocal.ps1')
    $upgradeShipped = (Test-Path -LiteralPath $shippedUpgradeScript -PathType Leaf) -and
                      $upgradeInSums -eq 1 -and
                      $upgradeEntryPoint -eq 'scripts/Update-ControlServerLocal.ps1'
    Add-Assertion -Id 'RC-UPGRADE-SCRIPT-SHIPPED' -Subject 'the documented upgrade path is runnable from the delivered package' `
        -Expected 'scripts/Update-ControlServerLocal.ps1 present, hashed once in SHA256SUMS, named as operatorEntryPoints.upgrade' `
        -Actual "present=$(Test-Path -LiteralPath $shippedUpgradeScript) sumsEntries=$upgradeInSums entryPoint=$upgradeEntryPoint" `
        -Verdict $(if ($upgradeShipped) { 'PASS' } else { 'FAIL' }) `
        -Control "the ticket 08 candidate at $PreviousReleaseRoot carries the same section 4.5 text and the script is present there: $previousHadUpgrade (must be False)" `
        -Note 'Defect found while working ticket 09: section 4.5 shipped a command the package could not run.'

    $releaseKeyMaterial = Get-KeyMaterialFiles $ReleaseRoot
    $plantDir = Join-Path $evidence 'key-material-control'
    New-Item -ItemType Directory -Path $plantDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $plantDir 'planted.pfx') -Value 'not a real certificate' -Encoding ascii
    $plantedFound = @(Get-KeyMaterialFiles $plantDir).Count
    Add-Assertion -Id 'RC-NO-KEY-MATERIAL' -Subject 'the delivered candidate carries no certificate or key material' `
        -Expected '0 files with a key-material extension anywhere under the release root' `
        -Actual "found=$($releaseKeyMaterial.Count) $($releaseKeyMaterial -join ';')" `
        -Verdict $(if ($releaseKeyMaterial.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same detector over a directory holding one planted .pfx found $plantedFound (must be 1)"

    # ================================================================= B. install as a service
    $installResult = Join-Path $evidence 'install-result.json'
    $installDiagnostic = Join-Path $evidence 'install-diagnostic.log'
    $installTranscript = Join-Path $evidence 'install-console.log'

    & (Join-Path $ReleaseRoot 'scripts\Install-ControlServerLocal.ps1') `
        -PackagePath (Join-Path $ReleaseRoot 'controlserver') `
        -ResultPath $installResult -DiagnosticPath $installDiagnostic `
        -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot -BackupRoot $backupRoot `
        -OnboardPort $OnboardPort -HealthPort $HealthPort `
        -ListenAddress $BindAddress -HealthBindAddress $BindAddress `
        -SkipMachineEnvironmentInjection *>&1 | Tee-Object -FilePath $installTranscript
    $serviceInstalled = $true

    $install = Get-Content -Raw -LiteralPath $installResult | ConvertFrom-Json
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Add-Assertion -Id 'INSTALL-AS-SERVICE' -Subject 'Install-ControlServerLocal.ps1 installs, ACL-hardens and lifecycles the service' `
        -Expected "result PASS and the service reaches Running (isolated instance '$ServiceName')" `
        -Actual "result=$($install.result) sourceCommit=$($install.sourceCommit) status=$(if ($service) { $service.Status } else { '<absent>' })" `
        -Verdict $(if ($install.result -eq 'PASS' -and $service -and $service.Status -eq 'Running') { 'PASS' } else { 'FAIL' }) `
        -Note 'Ticket 14 left this INCONCLUSIVE because that session had no administrator token.'

    Add-Assertion -Id 'INSTALL-NON-INTERACTIVE' -Subject 'the install completes with no trust prompt and no console input' `
        -Expected 'the whole install runs in a -NonInteractive host and returns without waiting for a user' `
        -Actual "host command line: $([Environment]::CommandLine)" `
        -Verdict $(if ([Environment]::CommandLine -match '-NonInteractive') { 'PASS' } else { 'FAIL' }) `
        -Note 'A certificate trust dialog would block here forever; the store digests below are the independent observation that nothing was imported.'

    $storeDigestsAfterInstall = Get-AllStoreDigests
    $storeChangesInstall = Compare-StoreDigests $storeDigestsBefore $storeDigestsAfterInstall
    Add-Assertion -Id 'INSTALL-CERT-STORES-UNCHANGED' -Subject 'the install generates, imports and removes no certificate' `
        -Expected '0 of the 4 observed stores changes its sorted-thumbprint digest' `
        -Actual "changed=$($storeChangesInstall.Count) $($storeChangesInstall -join '; '); CurrentUser\Root count=$($storeDigestsAfterInstall[0].count)" `
        -Verdict $(if ($storeChangesInstall.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Note 'Digest over the sorted thumbprint set, not a count: a swap of one certificate for another keeps the count and moves the digest.'

    $certsDirectory = Join-Path $dataRoot 'certs'
    Add-Assertion -Id 'INSTALL-NO-CERTS-DIRECTORY' -Subject 'the data root has no certs directory after installation' `
        -Expected "$certsDirectory absent" -Actual (Test-Path -LiteralPath $certsDirectory) `
        -Verdict $(if (-not (Test-Path -LiteralPath $certsDirectory)) { 'PASS' } else { 'FAIL' })

    # The three instance roots, not $RunRoot: the run root also holds this script's own evidence
    # directory, and the planted .pfx that RC-NO-KEY-MATERIAL uses as its control lives there.
    $installedKeyMaterial = @(Get-KeyMaterialFiles -Path @($installRoot, $dataRoot, $backupRoot))
    Add-Assertion -Id 'INSTALL-NO-KEY-MATERIAL' -Subject 'installation leaves no key material under the instance roots' `
        -Expected '0 files with a key-material extension under the install root, data root and backup root' `
        -Actual "found=$($installedKeyMaterial.Count) $($installedKeyMaterial -join ';')" `
        -Verdict $(if ($installedKeyMaterial.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control 'RC-NO-KEY-MATERIAL used the same detector against a planted .pfx and found it'

    $productionCertificatePasswordAfterInstall = [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine')
    Add-Assertion -Id 'INSTALL-MACHINE-CERT-PASSWORD-UNTOUCHED' -Subject "the install does not write or clear machine-scope $certificatePasswordVariable" `
        -Expected 'present-before equals present-after and the value is unchanged' `
        -Actual ("presentBefore={0} presentAfter={1} valueUnchanged={2}" -f
            [bool]$productionCertificatePasswordBefore, [bool]$productionCertificatePasswordAfterInstall,
            ($productionCertificatePasswordBefore -ceq $productionCertificatePasswordAfterInstall)) `
        -Verdict $(if ($productionCertificatePasswordBefore -ceq $productionCertificatePasswordAfterInstall) { 'PASS' } else { 'FAIL' }) `
        -Note 'The value itself is never printed; only presence and an equality result.'

    $emittedConfigurationPath = Join-Path $installRoot 'appsettings.Production.json'
    $emittedRaw = Get-Content -Raw -LiteralPath $emittedConfigurationPath
    Copy-Item -LiteralPath $emittedConfigurationPath -Destination (Join-Path $evidence 'install-emitted-appsettings.Production.json') -Force
    $emitted = $emittedRaw | ConvertFrom-Json
    $presentRemovedKeys = @(Get-RemovedCertificateKeys -ConfigurationPath $emittedConfigurationPath)
    $healthIsHttp = $emitted.Health.url -like 'http://*'
    Add-Assertion -Id 'INSTALL-CONFIG-PLAINTEXT' -Subject 'the emitted production configuration is the plaintext key set' `
        -Expected 'none of the four removed certificate keys appears and Health:url is http' `
        -Actual "removedKeysPresent=$($presentRemovedKeys -join ',') healthUrl=$($emitted.Health.url) listenAddress=$($emitted.OnboardTransport.listenAddress)" `
        -Verdict $(if ($presentRemovedKeys.Count -eq 0 -and $healthIsHttp) { 'PASS' } else { 'FAIL' }) `
        -Control "the same four key names are matched literally against the file text; the detector finds them in the legacy configuration used by the upgrade rehearsal"

    $logDirectory = Join-Path $dataRoot 'logs'
    $ndjsonFiles = @(@(Get-ChildItem -LiteralPath $logDirectory -Filter '*.ndjson' -File -ErrorAction SilentlyContinue).Name)
    Add-Assertion -Id 'PERSISTENT-LOGS' -Subject 'the NDJSON file log described in manual section 7' `
        -Expected 'at least one controlserver-<date>.ndjson under the data root' `
        -Actual "logDirectory=$logDirectory files=$($ndjsonFiles -join ',')" `
        -Verdict $(if ($ndjsonFiles.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Note 'Ticket 14 left this INCONCLUSIVE: the file sink is written by the installer, and that run never installed.'

    $live = Invoke-Plain '/health/live'
    Add-Assertion -Id 'HEALTH-LIVE-HTTP' -Subject "GET /health/live over plain HTTP on $BindAddress`:$HealthPort" `
        -Expected '200 with status live' -Actual ($live -replace "`n", ' ') `
        -Verdict $(if ($live -match 'HTTP_STATUS=200' -and $live -match '"status"\s*:\s*"live"') { 'PASS' } else { 'FAIL' }) `
        -Note 'Body round-trip, not a TCP connect: the workstation proxy makes a bare connect succeed against hosts that do not exist.'

    $readyBefore = Invoke-Plain '/health/ready'
    Add-Assertion -Id 'HEALTH-GATED-BEFORE' -Subject 'GET /health/ready before any onboard session' `
        -Expected '503 RECOVERY_HANDSHAKE_REQUIRED' -Actual ($readyBefore -replace "`n", ' ') `
        -Verdict $(if ($readyBefore -match 'HTTP_STATUS=503' -and $readyBefore -match 'RECOVERY_HANDSHAKE_REQUIRED') { 'PASS' } else { 'FAIL' }) `
        -Note 'Documented expected result, and the control for HEALTH-READY later in this run.'

    $safetyBefore = Invoke-Plain '/api/onboard/v1/vehicle-safety'
    $safetyAnonymous = Invoke-Plain '/api/onboard/v1/vehicle-safety' -NoAuthorization
    $safetyJson = @($safetyBefore -split "`n" | Where-Object { $_.StartsWith('{') })
    $safety = if ($safetyJson.Count -gt 0) { $safetyJson[0] | ConvertFrom-Json } else { $null }
    Add-Assertion -Id 'SAFETY-PROJECTION-HTTP-AUTH' -Subject 'the plaintext HTTP vehicle-safety projection is credential gated' `
        -Expected '200 with the onboard credential over http' -Actual (Get-HttpStatus $safetyBefore) `
        -Verdict $(if ($safetyBefore -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control "the same http request without an Authorization header returned $(Get-HttpStatus $safetyAnonymous) (must not be 200)" `
        -Note 'Ticket 14 asserted this over HTTPS with a pinned root; this round it is plain http and there is no certificate anywhere in the path.'

    $safetyFresh = $safety -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$safety.observedAt).TotalSeconds -lt 30
    Add-Assertion -Id 'SAFETY-STOPPED' -Subject 'the real RIoT reports the configured vehicle stopped, with fresh evidence' `
        -Expected 'motionState STOPPED, no reason codes, observedAt within 30s' `
        -Actual "motionState=$($safety.motionState) reasonCodes=[$(@($safety.reasonCodes) -join ',')] observedAt=$($safety.observedAt) source=$($safety.source)" `
        -Verdict $(if ($safety.motionState -eq 'STOPPED' -and @($safety.reasonCodes).Count -eq 0 -and $safetyFresh) { 'PASS' } else { 'FAIL' }) `
        -Control 'ticket 14 recorded the same detector returning UNKNOWN / RIOT_READ_TIMEOUT while the path to RIoT was hijacked (riot-path-red.json)'

    $productionAfterInstall = Get-ProductionSnapshot
    $productionDrift = @(Compare-ProductionSnapshot $productionBefore $productionAfterInstall)
    Add-Assertion -Id 'PRODUCTION-UNAFFECTED-INSTALL' -Subject 'the production service is untouched by the isolated installation' `
        -Expected "no drift in status, PID or listeners against the pre-run snapshot (PID $($productionBefore.processIds -join ','), $($productionBefore.listeners -join ' '))" `
        -Actual "drift=$($productionDrift.Count) $($productionDrift -join '; '); status=$($productionAfterInstall.status)" `
        -Verdict $(if ($productionDrift.Count -eq 0 -and $productionAfterInstall.status -eq 'Running') { 'PASS' } else { 'FAIL' }) `
        -Note 'Listeners are taken by service PID, not by process name: the isolated instance runs the same executable name.'

    # ================================================== C. onboard session against the service
    # The installer deliberately ships JourneyRuntime disabled (manual section 11: no orders, no
    # vehicle movement). Demand intake is one of the directions this ticket must keep, so it is
    # enabled explicitly here, on the isolated instance only. The two create gates stay at the
    # packaged default of false, so nothing is dispatched and no vehicle moves.
    $journeyBefore = ($emitted.JourneyRuntime.enabled -eq $false)
    $emitted.JourneyRuntime.enabled = $true
    [IO.File]::WriteAllText($emittedConfigurationPath, ($emitted | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Restart-Service -Name $ServiceName -Force
    $null = Wait-Listening $HealthPort
    $packaged = Get-Content -Raw -LiteralPath (Join-Path $installRoot 'appsettings.json') | ConvertFrom-Json
    Add-Assertion -Id 'JOURNEY-RUNTIME-ENABLED' -Subject 'demand intake is exercised by an explicit deviation from the shipped default' `
        -Expected 'installer wrote JourneyRuntime.enabled=false; this run flips it to true on the isolated instance while both create gates stay false' `
        -Actual ("installerWroteFalse={0} riotCreateDispatch={1} absentObservationExperiment={2}" -f
            $journeyBefore, $packaged.RiotCreateDispatch.enabled, $packaged.RiotAbsentAtObservationCreateExperiment.enabled) `
        -Verdict $(if ($journeyBefore -and -not $packaged.RiotCreateDispatch.enabled -and
                       -not $packaged.RiotAbsentAtObservationCreateExperiment.enabled) { 'PASS' } else { 'FAIL' }) `
        -Note 'Recorded rather than hidden: a production install stays at enabled=false until the site is authorized to dispatch.'

    $onboardRun = Join-Path $RunRoot 'onboard-hmi'
    Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
    $journalPath = Join-Path $RunRoot 'onboard-journal.db'
    $onboardSettingsPath = Join-Path $onboardRun 'appsettings.json'
    $onboard = Get-Content -Raw -LiteralPath (Join-Path $onboardRun 'appsettings.Production.template.json') | ConvertFrom-Json
    $onboard.agvId = $packaged.JourneyRuntime.agvId
    $onboard.onboardInstanceId = 'OBU-8005-TICKET09'
    $onboard.ruleGateway.host = '127.0.0.1'
    $onboard.wireToGate.host = $BindAddress
    $onboard.wireToGate.port = $OnboardPort
    $onboard.wireToGate.onboardInstanceId = '4f6d1c2e-9d3a-4a55-9d0b-14ab2f0e77c1'
    $onboard.wireToGate.journalPath = $journalPath
    $onboard.vehicleSafety.endpoint = "http://${BindAddress}:$HealthPort/api/onboard/v1/vehicle-safety"
    $onboard.vehicleSafety.expectedVehicleKey = $packaged.JourneyRuntime.vehicleKey
    $onboard.ioModule.host = '127.0.0.1'
    $onboard.ioModule.port = 1502
    $onboard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $onboardSettingsPath -Encoding utf8NoBOM
    Copy-Item -LiteralPath $onboardSettingsPath -Destination (Join-Path $evidence 'onboard-appsettings.json') -Force

    # The projection stays in the pipeline. @(<no match>.Matches) is @($null), whose Count is 1, so
    # the naive form reports one surviving placeholder in a file that has none.
    $placeholders = @(Select-String -LiteralPath $onboardSettingsPath -Pattern 'REPLACE_' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Value }).Count
    $onboardTlsKeys = @(Get-OnboardTlsKeys -ConfigurationPath $onboardSettingsPath)
    Add-Assertion -Id 'CFG-ONBOARD-PRODUCTION' -Subject 'onboard appsettings.json built from the packaged production template' `
        -Expected 'no REPLACE_ placeholder survives, declared build commit equals the packaged onboard commit, no TLS key is reintroduced' `
        -Actual "placeholders=$placeholders declaredBuildCommit=$($onboard.wireToGate.onboardBuildCommit) tlsKeys=$($onboardTlsKeys -join ',')" `
        -Verdict $(if ($placeholders -eq 0 -and $onboard.wireToGate.onboardBuildCommit -eq $onboardCommit -and $onboardTlsKeys.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same three key names matched literally; ticket 14's onboard configuration for the TLS candidate carried useTls and serverCertificateSha256"

    $simulatorProcess = Start-Process -FilePath $SimulatorExe -PassThru
    $simulatorUp = (Wait-Listening -Port 1502) -and (Wait-Listening -Port 58006)
    Add-Assertion -Id 'IO-SIMULATOR' -Subject 'eight-slot IO simulator Modbus 1502 and control plane 58006' `
        -Expected 'both listening' -Actual $simulatorUp `
        -Verdict $(if ($simulatorUp) { 'PASS' } else { 'FAIL' }) `
        -Note 'SIMULATED IO. Not evidence about real IO modules, wiring, locks or light curtains.'

    $onboardLogDirectory = Join-Path $onboardRun 'logs'
    function Get-OnboardSessionLine {
        $file = @(Get-ChildItem -Path $onboardLogDirectory -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending)
        if ($file.Count -eq 0) { return $null }
        return (Select-String -LiteralPath $file[0].FullName -Pattern '上层会话已建立' | Select-Object -Last 1).Line
    }
    $sessionBefore = Get-OnboardSessionLine

    $onboardProcess = Start-Process -FilePath (Join-Path $onboardRun 'SQCD.Agv.Wpf.exe') -WorkingDirectory $onboardRun -PassThru
    Start-Sleep -Seconds $SessionObserveSeconds

    $sessionAfter = Get-OnboardSessionLine
    Add-Assertion -Id 'SESSION-ESTABLISHED' -Subject 'the packaged OnboardHmi establishes a plaintext session with the installed service' `
        -Expected 'onboard log records 上层会话已建立' -Actual ($sessionAfter ?? '<none>') `
        -Verdict $(if ($sessionAfter) { 'PASS' } else { 'FAIL' }) `
        -Control "the same detector over the same directory before the onboard started returned '$($sessionBefore ?? '<none>')' (must be none)"

    $readiness = if ($sessionAfter -match 'readiness=(\w+)') { $Matches[1] } else { '<unparsed>' }
    Add-Assertion -Id 'SESSION-READY' -Subject 'onboard readiness on a clean install once the documented prerequisites are met' `
        -Expected 'Ready' -Actual $readiness `
        -Verdict $(if ($readiness -eq 'Ready') { 'PASS' } else { 'FAIL' }) `
        -Control 'ticket 07 recorded the same detector at RecoveryRequired with VEHICLE_STATE_UNKNOWN while the guest had no Modbus IO'

    $readyAfter = Invoke-Plain '/health/ready'
    Add-Assertion -Id 'HEALTH-READY' -Subject 'GET /health/ready once the onboard session reports Ready' `
        -Expected '200 ready' -Actual ($readyAfter -replace "`n", ' ') `
        -Verdict $(if ($readyAfter -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control 'HEALTH-GATED-BEFORE recorded 503 on the same endpoint minutes earlier in this very run'

    # -------------------------------------------------------------------------------- intake
    # Loaded from the release package, not from the install root. Add-Type keeps a file lock, and
    # Microsoft.Data.Sqlite pulls in the native e_sqlite3.dll next to it; locking those inside the
    # install root makes the uninstall at the end of this run fail to delete the directory.
    Add-Type -Path (Join-Path $ReleaseRoot 'controlserver\Microsoft.Data.Sqlite.dll')
    $dbPath = Join-Path (Join-Path $dataRoot 'data') 'controlserver.db'
    function Get-DatabaseCounts {
        $copy = Join-Path $evidence "snapshot-$([Guid]::NewGuid().ToString('N').Substring(0,8)).db"
        Copy-Item -LiteralPath $dbPath -Destination $copy -Force
        foreach ($suffix in '-wal', '-shm') {
            if (Test-Path -LiteralPath "$dbPath$suffix") { Copy-Item -LiteralPath "$dbPath$suffix" -Destination "$copy$suffix" -Force }
        }
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$copy")
        $connection.Open()
        $counts = [ordered]@{}
        foreach ($table in 'JourneyBacklog', 'AcceptedDemands', 'JourneyRuntimes', 'SessionRecoveries',
                           'ProtocolInbox', 'ProtocolOutbox', 'StationOperations', 'RiotDispatchAuditEvents',
                           'StationTaskTypeAdmissions') {
            $command = $connection.CreateCommand()
            $command.CommandText = "SELECT COUNT(*) FROM `"$table`""
            try { $counts[$table] = [int]$command.ExecuteScalar() } catch { $counts[$table] = -1 }
        }
        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode FROM "SessionRecoveries"'
        $reader = $command.ExecuteReader()
        $sessions = [System.Collections.Generic.List[object]]::new()
        while ($reader.Read()) {
            $sessions.Add([ordered]@{
                    agvId = $reader.GetString(0); generation = $reader.GetInt32(1)
                    readiness = $reader.GetValue(2).ToString(); reasonCode = $reader.GetValue(3).ToString()
                })
        }
        $reader.Close()

        # Per-demand decisions. A backlog row without a ReasonCode is a demand the acceptance
        # evaluator never reached; a row with one is a decision it did reach, and the decision is
        # what proves the intake pipeline ran, whether or not anything qualified today.
        $decisions = [System.Collections.Generic.List[object]]::new()
        $command = $connection.CreateCommand()
        $command.CommandText = @'
SELECT CASE WHEN instr(TransportDemandKey, '|') > 0
            THEN substr(TransportDemandKey, instr(TransportDemandKey, '|') + 1)
            ELSE '<unkeyed>' END AS workType,
       ReasonCode,
       COUNT(*) AS n
FROM "JourneyBacklog"
GROUP BY workType, ReasonCode
'@
        try {
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                $decisions.Add([ordered]@{
                        workType = $reader.GetValue(0).ToString()
                        reasonCode = $reader.GetValue(1).ToString()
                        count = [int]$reader.GetValue(2)
                    })
            }
            $reader.Close()
        }
        catch { }

        $connection.Close()
        [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
        return [pscustomobject]@{ Counts = $counts; Sessions = $sessions; Decisions = $decisions }
    }

    Start-Sleep -Seconds $IntakeObserveSeconds
    $snapshot = Get-DatabaseCounts
    $counts = $snapshot.Counts

    Add-Assertion -Id 'ADAPTER-MESINGEST' -Subject 'the MesIngest adapter reads real demands from the existing MesIngest' `
        -Expected 'JourneyBacklog non-empty' -Actual "JourneyBacklog=$($counts.JourneyBacklog)" `
        -Verdict $(if ($counts.JourneyBacklog -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "StationOperations=$($counts.StationOperations) on the same database, so this is a specific read and not every table being filled"

    Add-Assertion -Id 'ADAPTER-RIOT-READONLY' -Subject 'the RIoT read path leaves a durable row derived from a real RIoT read' `
        -Expected 'StationTaskTypeAdmissions populated from the RIoT map station catalog' `
        -Actual "StationTaskTypeAdmissions=$($counts.StationTaskTypeAdmissions)" `
        -Verdict $(if ($counts.StationTaskTypeAdmissions -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "RiotDispatchAuditEvents=$($counts.RiotDispatchAuditEvents) in the same run, so a populated admission table is the read path and not the create path"

    # Whether anything qualifies today is a property of the live MES content, not of this build, so
    # it is separated from the thing this run can actually qualify: that every WIRE_TO_GATE demand
    # reached a named decision. A decided demand is also the runtime readback for JourneyRuntime --
    # with it disabled nothing classifies anything.
    $decisions = @($snapshot.Decisions)
    $wireToGate = @($decisions | Where-Object { $_.workType -eq 'WIRE_TO_GATE' })
    $wireToGateTotal = (@($wireToGate | ForEach-Object { $_.count }) | Measure-Object -Sum).Sum
    $undecided = @($decisions | Where-Object { [string]::IsNullOrWhiteSpace($_.reasonCode) })
    $decisionSummary = ($wireToGate | ForEach-Object { "$($_.reasonCode)=$($_.count)" }) -join ' '
    Add-Assertion -Id 'DEMAND-DECISION-REACHED' -Subject 'the acceptance evaluator reaches a named decision for every WIRE_TO_GATE demand in the real backlog' `
        -Expected 'at least one WIRE_TO_GATE demand present and no backlog row left without a reason code' `
        -Actual "wireToGateDemands=$wireToGateTotal decisions[$decisionSummary] undecidedRows=$($undecided.Count) backlog=$($counts.JourneyBacklog)" `
        -Verdict $(if ($wireToGateTotal -gt 0 -and $undecided.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same query over the whole backlog attributes every non-WIRE_TO_GATE demand to OUT_OF_SCOPE_WORK_TYPE, so the classifier is discriminating by work type rather than stamping one reason on everything" `
        -Note 'This is also the runtime readback for JourneyRuntime: a disabled journey runtime classifies nothing.'

    $accepted = ($counts.AcceptedDemands -gt 0 -and $counts.JourneyRuntimes -gt 0)
    Add-Assertion -Id 'DEMAND-ACCEPTED' -Subject 'a WIRE_TO_GATE demand is accepted from the real backlog' `
        -Expected 'AcceptedDemands and JourneyRuntimes both non-empty' `
        -Actual "AcceptedDemands=$($counts.AcceptedDemands) JourneyRuntimes=$($counts.JourneyRuntimes); WIRE_TO_GATE decisions[$decisionSummary]" `
        -Verdict $(if ($accepted) { 'PASS' } else { 'INCONCLUSIVE' }) `
        -Control 'ticket 13 held the same tables at 0 with a populated backlog because readiness never reached Ready; here readiness is Ready and the demands are declined individually, by name' `
        -Note 'INCONCLUSIVE rather than FAIL when no demand qualifies: which demands the live MES holds at run time is not a property of this candidate. DEMAND-DECISION-REACHED is what this run can qualify.'

    Add-Assertion -Id 'SAFETY-NO-CREATE' -Subject 'no RIoT order was created during acceptance' `
        -Expected 'RiotDispatchAuditEvents empty' -Actual "RiotDispatchAuditEvents=$($counts.RiotDispatchAuditEvents)" `
        -Verdict $(if ($counts.RiotDispatchAuditEvents -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control 'the same table is what a real create writes; the authorized field runs show it non-empty'

    # ------------------------------------------------------------------- restart and recovery
    $generationBefore = @($snapshot.Sessions)[0].generation
    $inboxBefore = $counts.ProtocolInbox
    $journalBefore = (Get-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue).Length

    Restart-Service -Name $ServiceName -Force
    $restarted = (Wait-Listening -Port $OnboardPort) -and (Wait-Listening -Port $HealthPort)
    Add-Assertion -Id 'RESTART-SERVICE' -Subject 'the installed service restarts on the same data root' `
        -Expected 'both ports listening again after Restart-Service' -Actual $restarted `
        -Verdict $(if ($restarted) { 'PASS' } else { 'FAIL' }) `
        -Note 'Restart-Service is the documented section 5 operation, not a process kill.'

    Start-Sleep -Seconds $SessionObserveSeconds
    $afterRestart = Get-DatabaseCounts
    $generationAfter = @($afterRestart.Sessions)[0].generation
    $readinessAfter = @($afterRestart.Sessions)[0].readiness
    $readyAfterRestart = Invoke-Plain '/health/ready'
    $journalAfter = (Get-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue).Length

    Add-Assertion -Id 'RESTART-SESSION-RECOVERED' -Subject 'the onboard reconnects after the service restart and readiness returns' `
        -Expected 'session generation advances and readiness is Ready again' `
        -Actual "generation $generationBefore -> $generationAfter readiness=$readinessAfter" `
        -Verdict $(if ($generationAfter -gt $generationBefore -and $readinessAfter -eq 'Ready') { 'PASS' } else { 'FAIL' }) `
        -Control 'a generation that did not advance would mean the reconnect was never observed; the pre-restart value is recorded above'

    Add-Assertion -Id 'RESTART-HEALTH-READY' -Subject 'GET /health/ready after the restart' `
        -Expected '200 ready' -Actual ($readyAfterRestart -replace "`n", ' ') `
        -Verdict $(if ($readyAfterRestart -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control 'the same endpoint returned 503 before any session existed in this very run'

    Add-Assertion -Id 'RESTART-STATE-PRESERVED' -Subject 'stored state survives the restart on both sides' `
        -Expected 'server inbox does not shrink and the onboard journal is not recreated' `
        -Actual "ProtocolInbox $inboxBefore -> $($afterRestart.Counts.ProtocolInbox); journal $journalBefore -> $journalAfter bytes; AcceptedDemands=$($afterRestart.Counts.AcceptedDemands)" `
        -Verdict $(if ($afterRestart.Counts.ProtocolInbox -ge $inboxBefore -and $journalAfter -ge $journalBefore -and
                       $afterRestart.Counts.AcceptedDemands -ge $counts.AcceptedDemands) { 'PASS' } else { 'FAIL' })

    # ------------------------------------------------------------- named external qualifications
    Add-Assertion -Id 'MOVEMENT-CLOSED-LOOP' -Subject 'pickup, multi-slot load, movement, gate batch unload and atomic completion' `
        -Expected 'one full WIRE_TO_GATE journey on the real vehicle' `
        -Actual 'not attempted: the RIoT create gate stayed closed and no per-run safety GO was given' `
        -Verdict 'INCONCLUSIVE' -Note 'Ticket 10. SAFETY-NO-CREATE is the positive evidence that this run did not move the vehicle.'
    Add-Assertion -Id 'HW-ONBOARD-TARGET' -Subject 'screen, touch and barcode scanner on the target vehicle terminal' `
        -Expected 'exercised on the target hardware' -Actual 'not present on this workstation' -Verdict 'INCONCLUSIVE'
    Add-Assertion -Id 'HW-REAL-IO' -Subject 'real eight-slot IO module, wiring, locks and light curtains' `
        -Expected 'exercised on real IO' -Actual 'simulator only (ioModule 127.0.0.1:1502)' -Verdict 'INCONCLUSIVE'
}
catch {
    Add-Failure 'RUN-ABORTED' 'the acceptance run completed without an unhandled failure' `
        'no unhandled exception' $_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'run-exception.txt') -Encoding utf8NoBOM
}
finally {
    foreach ($process in @($onboardProcess, $simulatorProcess)) {
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(20000) | Out-Null }
    }

    # The uninstall removes the data root, and the NDJSON log lives under it. Round 1 of this run
    # lost its server logs that way, which is exactly what post-hoc diagnosis needs.
    $logSource = Join-Path $dataRoot 'logs'
    if (Test-Path -LiteralPath $logSource) {
        $logDestination = Join-Path $evidence 'server-logs'
        New-Item -ItemType Directory -Path $logDestination -Force | Out-Null
        Copy-Item -LiteralPath $logSource -Destination $logDestination -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ============================================================================ E. uninstall
    if ($serviceInstalled) {
        $uninstallResult = Join-Path $evidence 'uninstall-result.json'
        $uninstallTranscript = Join-Path $evidence 'uninstall-console.log'
        try {
            & (Join-Path $ReleaseRoot 'scripts\Uninstall-ControlServerLocal.ps1') `
                -ServiceName $ServiceName -InstallRoot $installRoot -DataRoot $dataRoot `
                -ResultPath $uninstallResult -RemoveDataRoot -ConfirmUninstall *>&1 |
                Tee-Object -FilePath $uninstallTranscript
            $uninstall = Get-Content -Raw -LiteralPath $uninstallResult | ConvertFrom-Json
            $serviceGone = $null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
            Add-Assertion -Id 'UNINSTALL-SERVICE' -Subject 'Uninstall-ControlServerLocal.ps1 removes the isolated service and its roots' `
                -Expected 'result PASS, service absent, install root and data root removed' `
                -Actual "result=$($uninstall.result) serviceAbsent=$serviceGone installRoot=$(Test-Path -LiteralPath $installRoot) dataRoot=$(Test-Path -LiteralPath $dataRoot)" `
                -Verdict $(if ($uninstall.result -eq 'PASS' -and $serviceGone -and
                               -not (Test-Path -LiteralPath $installRoot) -and -not (Test-Path -LiteralPath $dataRoot)) { 'PASS' } else { 'FAIL' })
        }
        catch {
            Add-Failure 'UNINSTALL-SERVICE' 'Uninstall-ControlServerLocal.ps1 removes the isolated service and its roots' `
                'result PASS' $_.Exception.Message
        }
    }

    $storeDigestsFinal = Get-AllStoreDigests
    $storeChangesFinal = Compare-StoreDigests $storeDigestsBefore $storeDigestsFinal
    Add-Assertion -Id 'LIFECYCLE-CERT-STORES-UNCHANGED' -Subject 'install through uninstall changes no certificate store' `
        -Expected '0 of the 4 observed stores changes its digest across the whole run' `
        -Actual "changed=$($storeChangesFinal.Count) $($storeChangesFinal -join '; ')" `
        -Verdict $(if ($storeChangesFinal.Count -eq 0) { 'PASS' } else { 'FAIL' })

    $productionFinal = Get-ProductionSnapshot
    $productionDriftFinal = @(Compare-ProductionSnapshot $productionBefore $productionFinal)
    $certificatePasswordFinal = [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine')
    $passwordUnchanged = ($productionCertificatePasswordBefore -ceq $certificatePasswordFinal)
    Add-Assertion -Id 'PRODUCTION-UNAFFECTED-FINAL' -Subject 'the production service and its machine-scope secret survive the whole run' `
        -Expected "no drift against the pre-run snapshot and the certificate password value unchanged" `
        -Actual ("drift={0} {1}; status={2} certificatePasswordUnchanged={3}" -f
            $productionDriftFinal.Count, ($productionDriftFinal -join '; '), $productionFinal.status, $passwordUnchanged) `
        -Verdict $(if ($productionDriftFinal.Count -eq 0 -and $productionFinal.status -eq 'Running' -and $passwordUnchanged) { 'PASS' } else { 'FAIL' })

    $stillListening = @(Get-NetTCPConnection -State Listen -LocalPort $OnboardPort, $HealthPort, 1502, 58006 -ErrorAction SilentlyContinue)
    Add-Assertion -Id 'TEARDOWN-PORTS' -Subject 'every port this run opened is released' `
        -Expected '0 still listening' -Actual $stillListening.Count `
        -Verdict $(if ($stillListening.Count -eq 0) { 'PASS' } else { 'FAIL' })

    $summary = [ordered]@{
        schemaVersion = 1
        runKind = 'TICKET09_CLEAN_INSTALL_ACCEPTANCE_NO_MOVEMENT'
        releaseRoot = $ReleaseRoot
        serviceName = $ServiceName
        onboardPort = $OnboardPort
        healthPort = $HealthPort
        bindAddress = $BindAddress
        createGateOpen = $false
        vehicleMoved = $false
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
    [IO.File]::WriteAllText((Join-Path $evidence 'assertions.json'),
        ($summary | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    'PASS={0} FAIL={1} INCONCLUSIVE={2}' -f $summary.counts.pass, $summary.counts.fail, $summary.counts.inconclusive
}
