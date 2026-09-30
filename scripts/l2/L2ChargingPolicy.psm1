#Requires -Version 7
Set-StrictMode -Version Latest

# Batch 9's ChargingPolicy setup key and default precondition (control-server#400, REQ-0282, specification 8.6).
#
# Since control-server#400 a vehicle with no approved, activated ChargingPolicyVersion covering it takes no new work. So
# every scenario gets one by default, after the server is up and before the scenario publishes its first demand, through
# the same three FieldOps verbs a site uses: import-charging-policy, approve-charging-policy (source L2_PRESET, never
# FIELD) and activate-charging-policy --allow-non-field-approval. No existing setup.psd1 changes for it.
#
# The default values are the test fixture's: completion 80, mandatory charge entry 30, minimum post-task margin 30,
# estimated consumption per task 0, every vehicle. Once control-server batch 9-05 judges "battery - estimate >= margin and
# not below the entry threshold", these agree with the retired MinimumBatteryPercent = 30 case for case, so no existing
# scenario's dispatch changes. The fake RIoT reports 80 % battery, so nothing here makes a vehicle want to charge. The
# charger roster is not imported by default: no version at all is an empty roster, which is a legal state.
#
# The ChargingPolicy key:
#   absent        the default policy above, for every vehicle
#   $false        import nothing: every vehicle stays not commissioned (the negative scenario's other half)
#   a table       @{ VehicleScope = @('<VehicleKey>', ...) } -- the default values, applied only to those vehicles

$script:DefaultPolicy = [ordered]@{
    minimumPostTaskBatteryMarginPercent  = 30
    mandatoryChargeEntryThresholdPercent = 30
    chargingCompletionThresholdPercent   = 80
    estimatedTaskConsumptionPercent      = 0
    progressStabilizationSeconds         = 180
    progressObservationWindowSeconds     = 600
    progressMinimumIncreasePercent       = 3
}

<#
What this run imports: $null for nothing, otherwise an ordered table with the policy file's fields.
#>
function Resolve-L2ChargingPolicy {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    $scope = [string[]]@()
    if ($Setup.ContainsKey('ChargingPolicy')) {
        $setting = $Setup.ChargingPolicy
        if ($setting -is [bool]) {
            if ($setting) {
                throw "ChargingPolicy = `$true in $Where means nothing: leave the key out for the default policy, give `$false for none, or @{ VehicleScope = @(...) }."
            }
            return $null
        }
        if ($setting -isnot [hashtable] -or @($setting.Keys | Where-Object { $_ -cne 'VehicleScope' }).Count -gt 0 -or
            -not $setting.ContainsKey('VehicleScope')) {
            throw "ChargingPolicy in $Where is `$false or a table with exactly one key, VehicleScope."
        }
        $scope = [string[]]@($setting.VehicleScope)
        if ($scope.Count -eq 0 -or @($scope | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
            throw "ChargingPolicy.VehicleScope in $Where lists at least one VehicleKey; leave the key out for every vehicle."
        }
    }
    $policy = [ordered]@{}
    foreach ($key in $script:DefaultPolicy.Keys) { $policy[$key] = $script:DefaultPolicy[$key] }
    $policy['vehicleScope'] = $scope
    $policy['changeNote'] = 'L2 preset (scripts/l2/L2ChargingPolicy.psm1): the test fixture values, never a field approval.'
    return $policy
}

<#
Imports, approves (L2_PRESET) and activates the policy through FieldOps. Returns the three outputs. Every verb that does
not answer OK throws: a rig that silently skipped this dispatches nothing, and that would look like a server defect.
#>
function Invoke-L2ChargingPolicyPreset {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Policy,
        [Parameter(Mandatory)][string[]]$Fleet,
        [Parameter(Mandatory)][scriptblock]$InvokeFieldOps,
        [Parameter(Mandatory)][string]$SnapshotRoot
    )

    $file = Join-Path $SnapshotRoot 'preseed-charging-policy.json'
    [IO.File]::WriteAllText($file, ($Policy | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $fleetText = $Fleet -join ';'

    $imported = & $InvokeFieldOps -Arguments @('import-charging-policy', '--input', $file, '--fleet', $fleetText)
    if ($imported.outcome -ne 'OK') { throw "Charging policy preset: import-charging-policy answered $($imported.outcome)." }
    $version = [string]$imported.version
    $approved = & $InvokeFieldOps -Arguments @('approve-charging-policy', '--version', $version, '--approved-by', 'L2 orchestrator',
        '--role', 'L2_PRESET', '--basis', 'scripts/l2/L2ChargingPolicy.psm1', '--source', 'L2_PRESET')
    if ($approved.outcome -ne 'OK') { throw "Charging policy preset: approve-charging-policy answered $($approved.outcome)." }
    $activated = & $InvokeFieldOps -Arguments @('activate-charging-policy', '--version', $version, '--activated-by', 'L2 orchestrator',
        '--fleet', $fleetText, '--allow-non-field-approval')
    if ($activated.outcome -ne 'OK') { throw "Charging policy preset: activate-charging-policy answered $($activated.outcome)." }
    return [pscustomobject]@{ Import = $imported; Approve = $approved; Activate = $activated; Version = [long]$version }
}

Export-ModuleMember -Function Resolve-L2ChargingPolicy, Invoke-L2ChargingPolicyPreset
