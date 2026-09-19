#Requires -Version 7

<#
.SYNOPSIS
    Helpers for the two batch 6 hold scenarios (control-server#162): drive one WIRE_TO_GATE demand through its legs,
    submit a hold through the dashboard's confirmation page, and read holds and the dashboard row back.

.DESCRIPTION
    Kept apart from Invoke-L2Scenario.ps1 and L2.psm1 on purpose: the orchestrator is shared by every scenario, and
    these are only what binding-hold-dashboard-not-cascading and catalog-change-binding-hold need.

    Every wait goes through Wait-L2Condition, so a hold that never appears is a timed-out criterion with the last
    value observed, not a single read that happened to be early.
#>

Set-StrictMode -Version Latest

# Same as L2Change.psm1: without -Force this reuses the copy the orchestrator already loaded.
Import-Module (Join-Path $PSScriptRoot 'L2.psm1')

<#
Publishes one WIRE_TO_GATE demand to the fake MesIngest and returns its ids. The area is the one the rig's pickup
station name resolves to (N1-3_N1-7), so the demand is admissible as far as the station is concerned.
#>
function New-L2WireToGateDemand {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$Label
    )

    $guid = [guid]::NewGuid()
    $demand = [pscustomobject]@{
        Label  = $Label
        Wire   = $guid.ToString('N')
        Id     = $guid.ToString('D')
        Sublot = "L2-SUBLOT-$($Context.RunId)-$Label"
    }
    $Context.Journal.Note("Publishing WIRE_TO_GATE demand $Label ($($demand.Wire)).")
    $null = $Context.MesIngest.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
    return $demand
}

function Get-L2JourneyStage {
    param([Parameter(Mandatory)][object]$Context, [Parameter(Mandatory)][object]$Demand)

    $rows = Invoke-L2Query -Connection $Context.Connection `
        -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$($Demand.Id)'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-L2Intent {
    param([Parameter(Mandatory)][object]$Context, [Parameter(Mandatory)][object]$Demand, [Parameter(Mandatory)][string]$Purpose)

    $rows = Invoke-L2Query -Connection $Context.Connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$($Demand.Id)' AND Purpose = '$Purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-L2BacklogReason {
    param([Parameter(Mandatory)][object]$Context, [Parameter(Mandatory)][object]$Demand)

    $rows = Invoke-L2Query -Connection $Context.Connection `
        -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$($Demand.Id)'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

<#
Takes an accepted demand from dispatch to the point where its TO_GATE order is created and confirmed: the vehicle
drives to the pickup, arrives, the synthetic peer loads and reports departure safety on its own. Returns the
TO_GATE intent.
#>
function Invoke-L2JourneyToGateLeg {
    param([Parameter(Mandatory)][object]$Context, [Parameter(Mandatory)][object]$Demand)

    $journal = $Context.Journal
    $riot = $Context.Riot
    $null = Wait-L2Condition -Description "demand $($Demand.Label) was accepted and dispatched to the pickup station" `
        -Journal $journal -Criterion "journey-stage-$($Demand.Label)" -TimeoutSeconds 90 `
        -Probe { Get-L2JourneyStage -Context $Context -Demand $Demand } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $pickup = Wait-L2Condition -Description "demand $($Demand.Label)'s TO_PICKUP intent was confirmed" `
        -Journal $journal -Criterion "to-pickup-intent-$($Demand.Label)" -TimeoutSeconds 60 `
        -Probe { $row = Get-L2Intent -Context $Context -Demand $Demand -Purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
        -Until { param($v) $null -ne $v }

    $journal.Note("Demand $($Demand.Label): vehicle drives to the pickup station and comes to rest.")
    $null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $pickup.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 5 })

    $null = Wait-L2Condition -Description "demand $($Demand.Label) reached its gate leg" `
        -Journal $journal -Criterion "gate-leg-$($Demand.Label)" -TimeoutSeconds 120 `
        -Probe { Get-L2JourneyStage -Context $Context -Demand $Demand } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    return Wait-L2Condition -Description "demand $($Demand.Label)'s TO_GATE intent was confirmed" `
        -Journal $journal -Criterion "to-gate-intent-$($Demand.Label)" -TimeoutSeconds 60 `
        -Probe { $row = Get-L2Intent -Context $Context -Demand $Demand -Purpose 'TO_GATE'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
        -Until { param($v) $null -ne $v }
}

<# Drives the vehicle from the gate leg to the gate and waits for the journey to complete. Returns the final stage. #>
function Complete-L2JourneyAtGate {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][object]$Demand,
        [Parameter(Mandatory)][object]$GateIntent
    )

    $riot = $Context.Riot
    $Context.Journal.Note("Demand $($Demand.Label): vehicle drives to the gate and comes to rest.")
    $null = $riot.Command('Put', "orders/$($GateIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $GateIntent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($GateIntent.UpperId)", @{ orderState = 5 })
    return Wait-L2Condition -Description "demand $($Demand.Label)'s journey completed at the gate" `
        -Journal $Context.Journal -Criterion "completed-$($Demand.Label)" -TimeoutSeconds 120 `
        -Probe { Get-L2JourneyStage -Context $Context -Demand $Demand } -Until { param($v) $v -eq 'Completed' }
}

<# Unreleased holds on the rig's map, oldest first. #>
function Get-L2TaskTypeHolds {
    param([Parameter(Mandatory)][object]$Context)

    $rows = Invoke-L2Query -Connection $Context.Connection -Sql (
        "SELECT HoldId, TaskType, Source, ReasonCode, DetailJson FROM TaskTypeStationHolds " +
        "WHERE MapId = $($Context.MapId) AND ReleasedAt IS NULL ORDER BY RaisedAt, HoldId")
    return , $rows
}

<# The active binding set's bindings on the rig's map, as one comparable string. #>
function Get-L2ActiveBindings {
    param([Parameter(Mandatory)][object]$Context)

    $rows = Invoke-L2Query -Connection $Context.Connection -Sql (
        "SELECT b.TaskType, b.StationRiotId, b.StationName, b.SiteVerificationRef, p.ActiveVersion " +
        "FROM TaskTypeStationActiveBindingSets p JOIN TaskTypeStationBindings b " +
        "ON b.MapId = p.MapId AND b.Version = p.ActiveVersion WHERE p.MapId = $($Context.MapId) ORDER BY b.TaskType")
    return (@($rows) | ForEach-Object { "v$($_.ActiveVersion) $($_.TaskType)=$($_.StationRiotId)/$($_.StationName)/$($_.SiteVerificationRef)" }) -join '; '
}

<#
Submits a hold through the dashboard as a person does: opens the row's confirmation page, then posts its form with the
dashboard's own Origin. Returns the confirmation page and the submission's status and Location.
#>
function Submit-L2DashboardHold {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$TaskType,
        [Parameter(Mandatory)][string]$Reason
    )

    $page = Invoke-WebRequest -NoProxy -TimeoutSec 10 `
        -Uri "$($Context.DashboardUrl)/actions/task-type-hold?mapId=$($Context.MapId)&taskType=$TaskType"
    $Context.Journal.Note("Submitting a dashboard hold on $TaskType ($Reason).")
    $submitted = Invoke-WebRequest -NoProxy -TimeoutSec 20 -Method Post -MaximumRedirection 0 -SkipHttpErrorCheck `
        -Uri "$($Context.DashboardUrl)/actions/task-type-hold" `
        -Headers @{ Origin = $Context.DashboardUrl } `
        -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
        -Body "mapId=$($Context.MapId)&taskType=$TaskType&reason=$([uri]::EscapeDataString($Reason))&claimedRole=$([uri]::EscapeDataString('L2 场景'))"
    return [pscustomobject]@{
        ConfirmationPage = [string]$page.Content
        StatusCode       = [int]$submitted.StatusCode
        Location         = [string]($submitted.Headers.Location | Select-Object -First 1)
    }
}

<# The decoded text of the dashboard's row for one task type in the binding card, or $null when there is none. #>
function Get-L2DashboardBindingRow {
    param([Parameter(Mandatory)][object]$Context, [Parameter(Mandatory)][string]$TaskType)

    $html = (Invoke-WebRequest -Uri $Context.DashboardUrl -NoProxy -TimeoutSec 10).Content
    $start = $html.IndexOf('id="task-type-bindings"', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $card = $html.Substring($start)
    $end = $card.IndexOf('</section>', [StringComparison]::Ordinal)
    if ($end -ge 0) { $card = $card.Substring(0, $end) }
    $match = [regex]::Match($card, '<tr><td>' + [regex]::Escape($TaskType) + '</td>.*?</tr>')
    if (-not $match.Success) { return $null }
    return [Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')).Trim() -replace '\s+', ' '
}

<# The whole dashboard page, for assertions about what it does not offer. #>
function Get-L2DashboardPage {
    param([Parameter(Mandatory)][object]$Context)

    return [string](Invoke-WebRequest -Uri $Context.DashboardUrl -NoProxy -TimeoutSec 10).Content
}

Export-ModuleMember -Function New-L2WireToGateDemand, Get-L2JourneyStage, Get-L2Intent, Get-L2BacklogReason,
    Invoke-L2JourneyToGateLeg, Complete-L2JourneyAtGate, Get-L2TaskTypeHolds, Get-L2ActiveBindings,
    Submit-L2DashboardHold, Get-L2DashboardBindingRow, Get-L2DashboardPage
