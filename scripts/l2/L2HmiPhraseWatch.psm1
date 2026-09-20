#Requires -Version 7

<#
L2HmiPhraseWatch: polls a real onboard's UIA tree for element names carrying a phrase, and keeps the
count of how many scans actually read the whole tree.

Added for control-server#204 in its own file, the way L2ConditionOrLast.psm1 was: shared L2 helpers each
take a new file instead of editing L2.psm1 or L2RealOnboard.psm1.

Why it exists. `L2-DA-09` (real-onboard-durable-ack-lost, onboard-hmi#124) asserts that a phrase never
appeared on the HMI, and backs that up with "and we looked at least 10 times". Written inline in the
scenario, a round in which an element's name could not be read still counted as a look, so "10 scans, 0
sightings" could also mean "10 rounds that read nothing at all". A negative assertion whose evidence is
the number of looks has to count only the looks that happened.

So a round is clean only when every element in it was read. A round in which any element threw, or in
which the tree could not be enumerated at all, is a failed round and is counted separately -- and the
assertion's "actual" column carries both numbers, so a red says "UIA read failed N rounds" rather than
"a wait timed out".

Nothing here throws. These scans ride along inside the scenario's business waits (the probe of a
Wait-L2Condition that is waiting for the journey to reach the gate leg), and a UIA exception thrown out
of such a probe is swallowed by Wait-L2Condition as a null reading -- which drags the business wait out
to its timeout and reports a timeout rather than the UIA failure. The business wait must see only its
own business condition.

`Seen` records a hit even in a failed round: the phrase was on the screen, and which round saw it does
not change that. Dropping hits from failed rounds is how this would go green while the thing it denies
is on the display.
#>

Set-StrictMode -Version Latest

# The default way to read one element's name: the UIA property whose value is the text WPF renders.
$script:DefaultNameReader = { param($Element) [string]$Element.Current.Name }

<#
A new watch over one phrase. Fields:

  Phrase          the text an element's name must contain to be a sighting
  CleanScans      rounds in which every element was read
  FailedScans     rounds in which the tree could not be enumerated, or an element's name could not be read
  FailedElements  elements whose name could not be read, across all rounds
  FewestElementsInACleanScan  the smallest number of elements any clean round read; $null before the first
                  one. A clean round that read only a handful is worth seeing in the criteria table.
  Seen            the distinct element names carrying the phrase, in the order first seen
#>
function New-L2HmiPhraseWatch {
    param([Parameter(Mandatory)][string]$Phrase)

    return [pscustomobject]@{
        Phrase         = $Phrase
        CleanScans     = 0
        FailedScans    = 0
        FailedElements = 0
        FewestElementsInACleanScan = $null
        Seen           = [System.Collections.Generic.List[string]]::new()
    }
}

<#
Scans once. $ElementSource returns this round's elements (throwing, or returning $null, when the window
is gone); $NameReader reads one element's name. Both are injectable so the self-check can drive this
without a rig.

Returns $true when the round was clean. Never throws.
#>
function Invoke-L2HmiPhraseScan {
    param(
        [Parameter(Mandatory)][object]$Watch,
        [Parameter(Mandatory)][scriptblock]$ElementSource,
        [scriptblock]$NameReader = $script:DefaultNameReader
    )

    $clean = $true
    $read = 0
    try {
        # The enumeration itself can throw halfway through -- an AutomationElementCollection is read
        # lazily -- so the loop is inside the same try as the call that produced it.
        $elements = & $ElementSource
        if ($null -eq $elements) {
            $clean = $false
        } else {
            foreach ($element in $elements) {
                try { $name = [string](& $NameReader $element) }
                catch { $Watch.FailedElements++; $clean = $false; continue }
                $read++
                if ($name.Contains($Watch.Phrase) -and -not $Watch.Seen.Contains($name)) { $Watch.Seen.Add($name) }
            }
        }
    } catch {
        $clean = $false
    }
    # Read nothing at all, and it was not a look -- whatever the reason. A live WPF main window has
    # dozens of elements, so a round that reads zero of them saw nothing rather than saw an empty screen.
    #
    # This line is what makes the guarantee structural instead of accidental. Without it the only thing
    # standing between "we looked 31 times" and "the tree was empty 31 times" is PowerShell unrolling an
    # empty collection into $null on the way out of $ElementSource -- which it does for some shapes and
    # not for others (`, @()` survives as an object), and which the call site can change without
    # touching anything here. The self-check pins the empty-collection case.
    if ($read -eq 0) { $clean = $false }
    if ($clean) {
        $Watch.CleanScans++
        if ($null -eq $Watch.FewestElementsInACleanScan -or $read -lt $Watch.FewestElementsInACleanScan) {
            $Watch.FewestElementsInACleanScan = $read
        }
    } else {
        $Watch.FailedScans++
    }
    return $clean
}

<#
Scans on a fixed cadence until $DurationSeconds is up or the phrase is seen, and returns a reading of
what the sampling did.

This is a sampling window, not a wait: the green run is the one in which nothing is ever seen, so
running to the end of the window is the expected outcome and must not be journalled as a condition that
was never reached.
#>
function Invoke-L2HmiPhraseSample {
    param(
        [Parameter(Mandatory)][object]$Watch,
        [Parameter(Mandatory)][scriptblock]$ElementSource,
        [Parameter(Mandatory)][int]$DurationSeconds,
        [scriptblock]$NameReader = $script:DefaultNameReader,
        [int]$PollMilliseconds = 250,
        [object]$Journal,
        [string]$Criterion
    )

    $before = $Watch.CleanScans + $Watch.FailedScans
    # 提前退出看的是**这个窗口里新增的**检出，不是累计检出。用累计的话，一个已经检出过的 watch 会让
    # 窗口在第一轮就退出，读数写成「10 s sampling done: 1 round(s)」——那句话在说「我扫了一轮」，
    # 而真相是「我一进来就发现之前已经检出过」。不造成假绿（已经检出就是红），但它正是这张票要消灭的
    # 那一类「数字比它知道的说得多」。
    $seenBefore = $Watch.Seen.Count
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
    while ($true) {
        $null = Invoke-L2HmiPhraseScan -Watch $Watch -ElementSource $ElementSource -NameReader $NameReader
        if ($Journal -and $Criterion) { $Journal.Observe($Criterion, $Watch.Seen.Count, $null) }
        if ($Watch.Seen.Count -gt $seenBefore) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    $rounds = $Watch.CleanScans + $Watch.FailedScans - $before
    $found = $Watch.Seen.Count - $seenBefore
    $reading = "${DurationSeconds} s sampling done: $rounds round(s), $found sighting(s) in this window" +
        $(if ($seenBefore -gt 0) { " ($seenBefore already seen before it)" } else { '' })
    if ($Journal) { $Journal.Note($reading) }
    return $reading
}

# One line for a criteria table's "actual" column.
function Format-L2HmiPhraseWatch {
    param([Parameter(Mandatory)][object]$Watch)

    $seen = if ($Watch.Seen.Count -gt 0) { ': ' + ($Watch.Seen -join ' | ') } else { '' }
    $fewest = if ($null -eq $Watch.FewestElementsInACleanScan) { '无干净轮' } else {
        "最少一轮读到 $($Watch.FewestElementsInACleanScan) 个元素" }
    return "干净扫描 $($Watch.CleanScans) 轮（$fewest）/ 失败 $($Watch.FailedScans) 轮（读失败元素 $($Watch.FailedElements) 个）/ " +
        "检出 $($Watch.Seen.Count) 次$seen"
}

Export-ModuleMember -Function New-L2HmiPhraseWatch, Invoke-L2HmiPhraseScan, Invoke-L2HmiPhraseSample,
    Format-L2HmiPhraseWatch
