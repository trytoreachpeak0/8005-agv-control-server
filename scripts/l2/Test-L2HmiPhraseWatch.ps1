#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2HmiPhraseWatch.psm1: a round that could not read everything is not a look, a UIA
    failure never reaches the business wait it rides in, and a sampling window that ends without a
    sighting is a reading rather than a condition that was never reached.

.DESCRIPTION
    Pure input, no rig and no UI Automation, a second. The element enumeration and the name reader are
    both injected, so every way the real tree can fail is constructed here.

    What this guards is `L2-DA-09` of real-onboard-durable-ack-lost (control-server#204, after the batch-6
    review of PR #196). That criterion denies something -- "the HMI never said the last load was
    unfinished" -- and the only thing standing between it and a vacuous green is the count of scans that
    actually read the tree. Every case below is one way the old inline version made "10 scans, 0
    sightings" mean less than it says:

      - an element whose name throws was skipped and the round still counted as a look;
      - the tree failing to enumerate threw out of the scan, into whichever business wait was carrying
        it, and arrived as that wait timing out;
      - a window that had gone away was neither a look nor a failed look;
      - the fixed 10-second sweep was written as a wait for a sighting, so the green run -- the one
        where nothing is ever seen -- always journalled "Not reached".

    The hit-in-a-failed-round case is the reverse guard: dropping a round's sightings because the round
    was dirty would make this whole watch go green while the phrase is on the display.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2HmiPhraseWatch.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2HmiPhraseWatch.psm1') -Force

$phrase = '上次装货操作未完成'

# An element whose name reads back as $Name, or throws when $Name is $null. Untyped on purpose: a
# [string] parameter turns $null into '', and the element that is supposed to fail would read fine.
function New-Element($Name) { return [pscustomobject]@{ Name = $Name } }
$reader = { param($Element) if ($null -eq $Element.Name) { throw 'ElementNotAvailable' } ; [string]$Element.Name }

# Stands in for L2Journal: records what a scenario would have written.
function New-Journal {
    $journal = [pscustomobject]@{ Notes = [System.Collections.Generic.List[string]]::new()
                                  Observations = [System.Collections.Generic.List[string]]::new() }
    $journal | Add-Member -MemberType ScriptMethod -Name Note -Value { param($Text) $this.Notes.Add([string]$Text) }
    $journal | Add-Member -MemberType ScriptMethod -Name Observe -Value {
        param($Criterion, $Value, $Detail) $this.Observations.Add("$Criterion=$Value") }
    return $journal
}

$cases = @()

$cases += @{
    Name  = 'a round that read every element is a clean look'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader `
            -ElementSource { @((New-Element '当前操作：装货'), (New-Element '日志：已连接')) }
        @{ Ok = ($watch.CleanScans -eq 1 -and $watch.FailedScans -eq 0 -and $watch.FailedElements -eq 0)
           Actual = "clean $($watch.CleanScans) / failed $($watch.FailedScans) / bad elements $($watch.FailedElements)" }
    }
}

$cases += @{
    Name  = 'a round in which one element could not be read is not a look'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader `
            -ElementSource { @((New-Element '当前操作：装货'), (New-Element $null), (New-Element '日志：已连接')) }
        @{ Ok = ($watch.CleanScans -eq 0 -and $watch.FailedScans -eq 1 -and $watch.FailedElements -eq 1)
           Actual = "clean $($watch.CleanScans) / failed $($watch.FailedScans) / bad elements $($watch.FailedElements)" }
    }
}

$cases += @{
    Name  = 'a tree that will not enumerate is a failed round, not an exception'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        try {
            $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader `
                -ElementSource { throw 'FindAll: ElementNotAvailable' }
            $threw = $false
        } catch { $threw = $true }
        @{ Ok = (-not $threw -and $watch.CleanScans -eq 0 -and $watch.FailedScans -eq 1)
           Actual = "threw=$threw / clean $($watch.CleanScans) / failed $($watch.FailedScans)" }
    }
}

$cases += @{
    Name  = 'a window that is gone is a failed round, not a silent nothing'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader -ElementSource { $null }
        @{ Ok = ($watch.CleanScans -eq 0 -and $watch.FailedScans -eq 1)
           Actual = "clean $($watch.CleanScans) / failed $($watch.FailedScans)" }
    }
}

$cases += @{
    Name  = 'a tree that enumerates to nothing is not a look either'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        # A real empty collection, not $null. UIA's FindAll can hand one back while the window is up but
        # its tree has not rendered, and whether PowerShell unrolls it into $null depends on how the call
        # site happens to be written. "We looked 31 times and saw nothing" must not be able to mean
        # "the tree was empty 31 times", and it must not rest on that unrolling either.
        1..12 | ForEach-Object {
            $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader -ElementSource { , @() }
        }
        @{ Ok = ($watch.CleanScans -eq 0 -and $watch.FailedScans -eq 12)
           Actual = "clean $($watch.CleanScans) / failed $($watch.FailedScans)" }
    }
}

$cases += @{
    Name  = 'a sighting in a failed round is still a sighting'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader `
            -ElementSource { @((New-Element $null), (New-Element "$phrase，需要管理员恢复")) }
        @{ Ok = ($watch.Seen.Count -eq 1 -and $watch.FailedScans -eq 1 -and $watch.CleanScans -eq 0)
           Actual = "seen $($watch.Seen.Count) / clean $($watch.CleanScans) / failed $($watch.FailedScans)" }
    }
}

$cases += @{
    Name  = 'a business probe carrying a failing scan still returns its business value'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        # The shape the scenario uses: the scan rides inside the probe of a business wait.
        $probe = {
            $null = Invoke-L2HmiPhraseScan -Watch $watch -NameReader $reader -ElementSource { throw 'FindAll: ElementNotAvailable' }
            'AwaitingGateArrival'
        }
        try { $value = & $probe ; $threw = $false } catch { $value = "threw: $($_.Exception.Message)" ; $threw = $true }
        @{ Ok = (-not $threw -and $value -eq 'AwaitingGateArrival' -and $watch.FailedScans -eq 1)
           Actual = "value='$value' threw=$threw / failed $($watch.FailedScans)" }
    }
}

$cases += @{
    Name  = 'a sampling window that ends without a sighting reports a reading, not "Not reached"'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $journal = New-Journal
        # 下界要与时长相称，而且要断挂钟。只断「扫过两轮」挡不住把采样循环改成「扫两轮就退出」或把
        # deadline 算错成毫秒——那种改法下这条会绿，而真装置那边 `L2-DA-09` 的 ≥10 轮靠后面三处业务探针
        # 也能凑够，所以真装置也照样绿。那正是 test.yml 里这一步声称要守的东西。
        # 1 秒 / 50 毫秒约 20 轮，取 10 留一半余量给慢机器。
        $started = [DateTimeOffset]::UtcNow
        $reading = Invoke-L2HmiPhraseSample -Watch $watch -NameReader $reader -DurationSeconds 1 -PollMilliseconds 50 `
            -Journal $journal -Criterion 'unfinished-projection' -ElementSource { @((New-Element '当前操作：装货')) }
        $elapsed = [DateTimeOffset]::UtcNow - $started
        $notReached = @($journal.Notes | Where-Object { $_ -like '*Not reached*' }).Count
        @{ Ok = ($notReached -eq 0 -and $watch.CleanScans -ge 10 -and $elapsed.TotalSeconds -ge 1 -and
                 $watch.Seen.Count -eq 0 -and $reading -like '*round(s)*' -and $reading -like '*0 sighting(s)*')
           Actual = "'Not reached' notes $notReached / clean $($watch.CleanScans) / 耗时 $([math]::Round($elapsed.TotalSeconds,2)) s / reading '$reading'" }
    }
}

$cases += @{
    Name  = 'a sampling window stops at the first sighting'
    Check = {
        $watch = New-L2HmiPhraseWatch -Phrase $phrase
        $journal = New-Journal
        $reading = Invoke-L2HmiPhraseSample -Watch $watch -NameReader $reader -DurationSeconds 30 -PollMilliseconds 50 `
            -Journal $journal -Criterion 'unfinished-projection' -ElementSource { @((New-Element "$phrase，需要管理员恢复")) }
        @{ Ok = ($watch.Seen.Count -eq 1 -and $watch.CleanScans -eq 1 -and $reading -like '*1 sighting(s)*')
           Actual = "seen $($watch.Seen.Count) / clean $($watch.CleanScans) / reading '$reading'" }
    }
}

$wrong = 0
foreach ($case in $cases) {
    try { $result = & $case.Check } catch { $result = @{ Ok = $false; Actual = "threw: $($_.Exception.Message)" } }
    if (-not $result.Ok) { $wrong++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($result.Ok) { 'ok  ' } else { 'BAD ' }), $case.Name, $result.Actual)
}

if ($wrong -gt 0) {
    Write-Host "L2HmiPhraseWatch self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2HmiPhraseWatch self-check: all $($cases.Count) cases as expected."
