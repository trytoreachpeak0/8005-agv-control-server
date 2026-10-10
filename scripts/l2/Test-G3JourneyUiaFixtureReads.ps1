#Requires -Version 7

<#
.SYNOPSIS
    Self-check for control-server#560: the journey G3 rig keeps "the fixture failed to write or read the HMI" apart from "the
    product answered", in g3-multi-stop-plan's sublot entry and g3-waiting-point-idle-return's held reading.

.DESCRIPTION
    Seconds, no rig, no window, no database. The functions under test take the onboard driver as an object, so a fake one
    with the same ScriptMethods stands in for the WPF; the journal is a real L2Journal on a temporary file, read back to show
    that every fixture retry was recorded.

      A. Invoke-L2SublotEntry / Wait-L2SubmitOutcome (scenarios/MultiStopRigCommon.ps1)
         - a box that does not read back the sublot is typed again and each typing is journaled; only a box that reads it
           back is submitted, exactly once;
         - a box that never reads it back ends as RIG_FIXTURE with nothing submitted;
         - a submit the onboard refuses (its app-log line) fails as PRODUCT_REFUSED with the box text, the code and the
           time, and is not submitted again; a refusal logged before the submit is not taken for this one.
      B. Read-L2UiaItemStatus (L2MultiStopJourney.psm1) and the scenario's held window
         - an element that cannot be read (not found, or throwing) is re-read, at most 3 reads no more than 0.5 s apart,
           each journaled; a readable value after that is returned as read;
         - a value actually read -- a wrong one, or the empty string -- is the product's: returned at once, never re-read;
         - an element unreadable on every read is reported unreadable, never as a value;
         - g3-waiting-point-idle-return.ps1 (read from its AST) ends its held window on an unreadable reading or a read
           value other than AT_WAITING_POINT, and G3-12-07 judges the held window's reading, not the first one.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-G3JourneyUiaFixtureReads.ps1
#>
[CmdletBinding()]
param(
    [string]$IdleReturnScenario = (Join-Path $PSScriptRoot 'scenarios\g3-waiting-point-idle-return.ps1')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'scenarios\MultiStopRigCommon.ps1')

$wrong = 0
function Test-Case([string]$Name, [scriptblock]$Body) {
    $failure = $null
    try { $null = & $Body } catch { $failure = $_.Exception.Message }
    if ($null -ne $failure) { $script:wrong++ }
    Write-Host ("{0}  {1}{2}" -f $(if ($null -eq $failure) { 'ok  ' } else { 'BAD ' }), $Name,
        $(if ($failure) { " -> $failure" } else { '' }))
}
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$scratch = Join-Path ([IO.Path]::GetTempPath()) "cs560-selfcheck-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$null = New-Item -ItemType Directory -Path $scratch

function New-Journal([string]$Name) { return New-L2Journal -Path (Join-Path $scratch "$Name.jsonl") }
function Get-Notes([object]$Journal) {
    return , @(Get-Content -LiteralPath $Journal.Path | ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.PSObject.Properties['note'] } | ForEach-Object { [string]$_.note })
}

<#
A fake scan box: $Landings says, per SetSublot call, what the box then reads back ('<typed>' for what was typed, anything else
literally, $null for unreadable). Calls are counted.
#>
function New-FakeEntryOnboard([object[]]$Landings) {
    $fake = [pscustomobject]@{ Landings = $Landings; Typed = 0; Submits = 0; Box = $null }
    $fake | Add-Member -MemberType ScriptMethod -Name SetSublot -Value {
        param([string]$Sublot)
        $landing = $this.Landings[[Math]::Min($this.Typed, $this.Landings.Count - 1)]
        $this.Typed++
        $this.Box = if ($landing -eq '<typed>') { $Sublot } else { $landing }
    }
    $fake | Add-Member -MemberType ScriptMethod -Name ScanText -Value { return $this.Box }
    $fake | Add-Member -MemberType ScriptMethod -Name SubmitReady -Value { return $true }
    $fake | Add-Member -MemberType ScriptMethod -Name Submit -Value { $this.Submits++ }
    return $fake
}

# --- A. sublot entry -------------------------------------------------------------------------------------------------

$sublot = 'G3-08-B-selfcheck'

Test-Case 'A1 fixture: a box that read back empty is typed again, journaled, then submitted once' {
    $journal = New-Journal 'a1'
    $fake = New-FakeEntryOnboard @('', '<typed>')
    $entry = Invoke-L2SublotEntry -Onboard $fake -Journal $journal -Sublot $sublot -Label 'B'
    $notes = Get-Notes $journal
    Assert-That ($fake.Typed -eq 2) "typed $($fake.Typed) times"
    Assert-That ($fake.Submits -eq 1) "submitted $($fake.Submits) times"
    Assert-That ($entry.Text -ceq $sublot -and $entry.Typings -eq 2) "entry $($entry | ConvertTo-Json -Compress)"
    Assert-That (@($notes | Where-Object { $_ -like "*read back after typing 1*''*" }).Count -eq 1) "notes: $($notes -join ' | ')"
    Assert-That (@($notes | Where-Object { $_ -like '*typing 2 of 3*' }).Count -eq 1) "no note for the second typing: $($notes -join ' | ')"
}

Test-Case 'A2 fixture: a box that changed between typing and submit is typed again, not submitted with the wrong text' {
    $journal = New-Journal 'a2'
    $fake = New-FakeEntryOnboard @('<typed>', '<typed>')
    # The first read-back after typing is right; the box is then overwritten before the pre-submit read.
    $fake | Add-Member -MemberType NoteProperty -Name Reads -Value 0
    $fake | Add-Member -MemberType ScriptMethod -Name ScanText -Force -Value {
        $this.Reads++
        if ($this.Reads -eq 2) { return 'G3-08-A-selfcheck' }
        return $this.Box
    }
    $entry = Invoke-L2SublotEntry -Onboard $fake -Journal $journal -Sublot $sublot -Label 'B'
    Assert-That ($fake.Typed -eq 2 -and $fake.Submits -eq 1 -and $entry.Text -ceq $sublot) "typed $($fake.Typed), submits $($fake.Submits), text $($entry.Text)"
    Assert-That (@((Get-Notes $journal) | Where-Object { $_ -like "*just before submit*'G3-08-A-selfcheck'*" }).Count -eq 1) 'the wrong pre-submit read was not journaled'
}

Test-Case 'A3 fixture: a box that never reads back the sublot ends RIG_FIXTURE after 3 typings, nothing submitted' {
    $journal = New-Journal 'a3'
    $fake = New-FakeEntryOnboard @($null)
    $message = $null
    try { $null = Invoke-L2SublotEntry -Onboard $fake -Journal $journal -Sublot $sublot -Label 'B' } catch { $message = $_.Exception.Message }
    Assert-That ($message -like 'RIG_FIXTURE:*') "message: $message"
    Assert-That ($fake.Typed -eq 3 -and $fake.Submits -eq 0) "typed $($fake.Typed), submits $($fake.Submits)"
    Assert-That (@((Get-Notes $journal) | Where-Object { $_ -like '*(unreadable)*' }).Count -eq 3) 'three unreadable read-backs not journaled'
}

function Write-AppLog([string]$Directory, [string[]]$Lines) {
    $null = New-Item -ItemType Directory -Path $Directory -Force
    [IO.File]::WriteAllLines((Join-Path $Directory 'agv-20261010.log'), $Lines, [Text.UTF8Encoding]::new($false))
}

Test-Case 'A4 product: a refused submit fails PRODUCT_REFUSED with box text, code and time, and is not submitted again' {
    $journal = New-Journal 'a4'
    $fake = New-FakeEntryOnboard @('<typed>')
    $entry = Invoke-L2SublotEntry -Onboard $fake -Journal $journal -Sublot $sublot -Label 'B'
    $logDir = Join-Path $scratch 'a4-onboard-app'
    $refusedAt = $entry.SubmittedAt.AddMilliseconds(560)
    Write-AppLog $logDir @(
        "$($entry.SubmittedAt.AddSeconds(-30).ToString('o'))`tWarning`tMainViewModel`t界面命令被业务规则拒绝：SOME_EARLIER_REFUSAL。",
        "$($refusedAt.ToString('o'))`tWarning`tMainViewModel`t界面命令被业务规则拒绝：SUBLOT_NOT_IN_WORKLIST。")
    $message = $null
    try {
        $null = Wait-L2SubmitOutcome -GetOperation { $null } -LogDirectory $logDir -Entry $entry -Journal $journal -Label 'B' -TimeoutSeconds 5
    } catch { $message = $_.Exception.Message }
    Assert-That ($message -like 'PRODUCT_REFUSED:*') "message: $message"
    Assert-That ($message -like '*SUBLOT_NOT_IN_WORKLIST*' -and $message -notlike '*SOME_EARLIER_REFUSAL*') "code: $message"
    Assert-That ($message -like "*'$sublot'*" -and $message -like "*$($refusedAt.ToString('o'))*") "text or time missing: $message"
    Assert-That ($fake.Submits -eq 1 -and $fake.Typed -eq 1) "typed $($fake.Typed), submits $($fake.Submits)"
}

Test-Case 'A5 product: a refusal logged before the submit is not this submit''s; the operation is returned' {
    $journal = New-Journal 'a5'
    $entry = [pscustomobject]@{ Text = $sublot; SubmittedAt = [DateTimeOffset]::Now; Typings = 1 }
    $logDir = Join-Path $scratch 'a5-onboard-app'
    Write-AppLog $logDir @("$($entry.SubmittedAt.AddSeconds(-1).ToString('o'))`tWarning`tMainViewModel`t界面命令被业务规则拒绝：SUBLOT_NOT_IN_WORKLIST。")
    $script:polls = 0
    $operation = Wait-L2SubmitOutcome -GetOperation { $script:polls++; if ($script:polls -ge 3) { [pscustomobject]@{ SlotOperationAttemptId = 'op-1' } } } `
        -LogDirectory $logDir -Entry $entry -Journal $journal -Label 'B' -TimeoutSeconds 10
    Assert-That ([string]$operation.SlotOperationAttemptId -eq 'op-1') "returned $($operation | ConvertTo-Json -Compress)"
}

# --- B. held reading -------------------------------------------------------------------------------------------------

<#
A fake onboard for Element: $Reads says, per call, what the element read gives -- '<missing>' for no element, '<throw>' for
ElementNotAvailableException, anything else as the ItemStatus. The last entry repeats. Call times are kept.
#>
function New-FakeStatusOnboard([object[]]$Reads) {
    $fake = [pscustomobject]@{ Reads = $Reads; Calls = 0; At = [System.Collections.Generic.List[DateTimeOffset]]::new() }
    $fake | Add-Member -MemberType ScriptMethod -Name Element -Value {
        param([string]$By, [string]$Value)
        $read = $this.Reads[[Math]::Min($this.Calls, $this.Reads.Count - 1)]
        $this.Calls++
        $this.At.Add([DateTimeOffset]::UtcNow)
        if ($read -eq '<missing>') { return $null }
        if ($read -eq '<throw>') { throw [System.Windows.Automation.ElementNotAvailableException]::new('gone under the read') }
        return [pscustomobject]@{ Current = [pscustomobject]@{ ItemStatus = $read } }
    }
    return $fake
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Test-Case 'B1 fixture: not found, then throwing, then AT_WAITING_POINT -- re-read, both failures journaled, value returned' {
    $journal = New-Journal 'b1'
    $fake = New-FakeStatusOnboard @('<missing>', '<throw>', 'AT_WAITING_POINT')
    $reading = Read-L2UiaItemStatus $fake 'IdleReturnStatus' $journal
    Assert-That ($reading.Readable -and $reading.Value -eq 'AT_WAITING_POINT' -and $reading.Reads -eq 3) "reading $($reading | ConvertTo-Json -Compress)"
    $notes = @((Get-Notes $journal) | Where-Object { $_ -like 'UIA read*IdleReturnStatus unreadable*' })
    Assert-That ($notes.Count -eq 2) "journaled: $($notes -join ' | ')"
    Assert-That ($notes[1] -like '*ElementNotAvailableException*') "exception not named: $($notes[1])"
    $gaps = @(1..($fake.At.Count - 1) | ForEach-Object { ($fake.At[$_] - $fake.At[$_ - 1]).TotalMilliseconds })
    Assert-That (@($gaps | Where-Object { $_ -gt 750 }).Count -eq 0) "gaps ms: $($gaps -join ',')"
}

Test-Case 'B2 fixture: unreadable on all 3 reads is reported unreadable with its reason, never as a value' {
    $journal = New-Journal 'b2'
    $fake = New-FakeStatusOnboard @('<missing>')
    $reading = Read-L2UiaItemStatus $fake 'IdleReturnStatus' $journal
    Assert-That (-not $reading.Readable -and $null -eq $reading.Value -and $reading.Reads -eq 3 -and $fake.Calls -eq 3) "reading $($reading | ConvertTo-Json -Compress), calls $($fake.Calls)"
    Assert-That ((Format-L2UiaReading $reading) -like '(unreadable after 3 reads: element not found)') (Format-L2UiaReading $reading)
}

Test-Case 'B3 product: a wrong value read is returned at once, not re-read' {
    $journal = New-Journal 'b3'
    $fake = New-FakeStatusOnboard @('EN_ROUTE_TO_WAITING_POINT', 'AT_WAITING_POINT')
    $reading = Read-L2UiaItemStatus $fake 'IdleReturnStatus' $journal
    Assert-That ($reading.Readable -and $reading.Value -eq 'EN_ROUTE_TO_WAITING_POINT' -and $fake.Calls -eq 1) "reading $($reading | ConvertTo-Json -Compress), calls $($fake.Calls)"
}

Test-Case 'B4 product: an empty string read is the product''s value, returned at once, not re-read' {
    $journal = New-Journal 'b4'
    $fake = New-FakeStatusOnboard @('', 'AT_WAITING_POINT')
    $reading = Read-L2UiaItemStatus $fake 'IdleReturnStatus' $journal
    Assert-That ($reading.Readable -and $reading.Value -ceq '' -and $fake.Calls -eq 1) "reading $($reading | ConvertTo-Json -Compress), calls $($fake.Calls)"
    Assert-That ((Format-L2UiaReading $reading) -eq "''") (Format-L2UiaReading $reading)
}

# The scenario's held window and G3-12-07, read from its AST: the Until block and the assertion's condition.
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $IdleReturnScenario).Path, [ref]$tokens, [ref]$errors)
$held = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and
        $n.GetCommandName() -eq 'Wait-L2ConditionOrLast' -and $n.Extent.Text -like "*'hmi-still-at-point'*" }, $true)
function Get-NamedArgument([object]$Command, [string]$Name) {
    $elements = $Command.CommandElements
    for ($i = 0; $i -lt $elements.Count - 1; $i++) {
        if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq $Name) {
            return $elements[$i + 1]
        }
    }
    return $null
}

Test-Case 'B5 scenario: g3-waiting-point-idle-return.ps1 parses' {
    Assert-That ($errors.Count -eq 0) "$($errors.Count) parse errors: $($errors | ForEach-Object { $_.Message })"
}

Test-Case 'B6 scenario: the held window reads through Read-L2UiaItemStatus and its Until fires on unreadable or a read value other than AT_WAITING_POINT' {
    Assert-That ($null -ne $held) 'no hmi-still-at-point wait'
    $probe = Get-NamedArgument $held 'Probe'
    Assert-That ($probe.Extent.Text -like '*Read-L2UiaItemStatus*IdleReturnStatus*') "probe: $($probe.Extent.Text)"
    $until = (Get-NamedArgument $held 'Until').ScriptBlock.GetScriptBlock()
    $atPoint = 'AT_WAITING_POINT'
    $cases = @(
        @{ v = [pscustomobject]@{ Readable = $true; Status = 'AT_WAITING_POINT'; CanSubmit = $false }; fires = $false }
        @{ v = [pscustomobject]@{ Readable = $true; Status = ''; CanSubmit = $false }; fires = $true }
        @{ v = [pscustomobject]@{ Readable = $true; Status = 'EN_ROUTE_TO_WAITING_POINT'; CanSubmit = $false }; fires = $true }
        @{ v = [pscustomobject]@{ Readable = $false; Status = $null; CanSubmit = $false }; fires = $true }
        @{ v = [pscustomobject]@{ Readable = $true; Status = 'AT_WAITING_POINT'; CanSubmit = $true }; fires = $true }
    )
    foreach ($case in $cases) {
        $fires = [bool](& $until $case.v)
        Assert-That ($fires -eq $case.fires) "Until($($case.v | ConvertTo-Json -Compress)) = $fires"
    }
}

Test-Case 'B7 scenario: G3-12-07 judges the held window''s reading ($heldReading.Readable and $heldAtPoint), not the first one' {
    $add = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $n.Member.Value -eq 'Add' -and $n.Arguments.Count -ge 3 -and $n.Arguments[0].Extent.Text -eq "'G3-12-07'" }, $true)
    Assert-That ($null -ne $add) 'no G3-12-07 assertion'
    $condition = $add.Arguments[2].Extent.Text
    Assert-That ($condition -like '*$heldReading.Readable*' -and $condition -like '*$heldAtPoint -eq $atPoint*') "condition: $condition"
    Assert-That ($condition -notlike '*$shownAtPoint*') "still reads the first reading: $condition"
}

Remove-Item -LiteralPath $scratch -Recurse -Force
if ($wrong -gt 0) {
    Write-Host "$wrong case(s) came out the other way."
    exit 1
}
Write-Host 'All cases as expected.'
exit 0
