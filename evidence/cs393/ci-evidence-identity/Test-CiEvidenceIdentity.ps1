#Requires -Version 7
<#
control-server#393: checks every assertions.json under a downloaded CI L2 artifact for the exit identity.
Prints one summary per field and every deviating file. Exit 0 when all hold.
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$ControlServerCommit,
    [string]$OnboardCommit = '',
    [string]$SimulatorCommit = '',
    # Only directories whose name starts with this (the committed copies sit side by side under evidence/l2/).
    [string]$DirectoryPrefix = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter assertions.json | Where-Object { -not $DirectoryPrefix -or $_.Directory.Name.StartsWith($DirectoryPrefix) })
$bad = [Collections.Generic.List[string]]::new()
$batches = @{}
foreach ($f in $files) {
    $a = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -AsHashtable
    $id = $a['identity']
    $p = $id['protocolReleaseIdentity']
    $batch = [string]$id['batchId']
    $batches[$batch] = 1 + ($batches[$batch] ?? 0)
    $problems = @()
    if ($a['outcome'] -ne 'PASS') { $problems += "outcome=$($a['outcome'])" }
    if ($p['tag'] -ne 'protocol-v3.0.0') { $problems += "tag=$($p['tag'])" }
    if ($p['approvalStatus'] -ne 'APPROVED_RELEASE') { $problems += "approval=$($p['approvalStatus'])" }
    if ($p['commit'] -ne '3f091cb2eae7c58cec54a95dd9389c9180bc7b4c') { $problems += "protocolCommit=$($p['commit'])" }
    if ($p['manifestSha256'] -ne 'd5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e') { $problems += 'manifest' }
    if ($p['profileId'] -ne 'AGV_FULL_PRODUCT' -or $p['protocolVersion'] -ne 4) { $problems += "profile=$($p['profileId'])/$($p['protocolVersion'])" }
    if ($id['controlServerCommit'] -ne $ControlServerCommit) { $problems += "controlServer=$($id['controlServerCommit'])" }
    if ($OnboardCommit -and $id['onboardHmiCommit'] -ne $OnboardCommit) { $problems += "onboard=$($id['onboardHmiCommit'])" }
    if ($SimulatorCommit -and $id['slotsSimulatorCommit'] -ne $SimulatorCommit) { $problems += "simulator=$($id['slotsSimulatorCommit'])" }
    if ($problems) { $bad.Add("$($f.Directory.Name): $($problems -join '; ')") }
}
"files: $($files.Count)"
"batchId counts: " + (($batches.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')
"deviating: $($bad.Count)"
$bad | ForEach-Object { "  $_" }
exit ($bad.Count -eq 0 ? 0 : 1)
