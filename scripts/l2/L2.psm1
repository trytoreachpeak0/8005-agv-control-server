#Requires -Version 7

<#
The L2 scenario orchestrator's shared machinery: bring an environment up, drive the doubles'
control planes, assert against the server's own database, and write evidence in the shape the G3
runs already use.

Two rules run through all of it.

  Never sleep to wait for something. Every wait is a predicate with a deadline, so a slow machine
  is slower rather than flaky, and a stuck run says which predicate never came true.

  Never assert through a shortcut. State comes from the server's SQLite store and the doubles'
  snapshot endpoints, both of which a scenario reads exactly as an operator would.
#>

Set-StrictMode -Version Latest

class L2Journal {
    [string]$Path
    [hashtable]$Last = @{}

    L2Journal([string]$path) {
        $this.Path = $path
        [IO.File]::WriteAllText($path, '', [Text.UTF8Encoding]::new($false))
    }

    # Append-only, one observation per line, and only when a criterion actually flips. That shape
    # comes from remote-ops/status/Get-WireToGateStatus.ps1, which is what let the 2026-09-03
    # investigation pin "12:56:49 STOPPED -> 12:57:15 UNKNOWN" to the second.
    [void] Observe([string]$criterion, [object]$value, [hashtable]$detail) {
        $rendered = if ($null -eq $value) { '(null)' } else { [string]$value }
        if ($this.Last.ContainsKey($criterion) -and $this.Last[$criterion] -eq $rendered) {
            return
        }
        $this.Last[$criterion] = $rendered
        $line = [ordered]@{
            at        = [DateTimeOffset]::UtcNow.ToString('o')
            criterion = $criterion
            value     = $rendered
        }
        if ($detail) { $line['detail'] = $detail }
        Add-Content -LiteralPath $this.Path -Value ($line | ConvertTo-Json -Compress -Depth 8) -Encoding utf8NoBOM
    }

    [void] Note([string]$message) {
        $line = [ordered]@{
            at      = [DateTimeOffset]::UtcNow.ToString('o')
            note    = $message
        }
        Add-Content -LiteralPath $this.Path -Value ($line | ConvertTo-Json -Compress -Depth 8) -Encoding utf8NoBOM
    }
}

function New-L2Journal {
    param([Parameter(Mandatory)][string]$Path)
    return [L2Journal]::new($Path)
}

<#
Waits for a predicate, polling on a fixed cadence until a deadline. Returns the last value the
predicate produced so a caller can assert on it without reading the world a second time.
#>
function Wait-L2Condition {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 60,
        [int]$PollMilliseconds = 250,
        [L2Journal]$Journal,
        [string]$Criterion,
        # The Start-L2Process handle of the component this condition depends on, when there is one.
        # A component that has already exited will never satisfy the condition, so waiting out the
        # timeout only delays the failure and reports "(nothing)" in place of its cause.
        [object]$Component
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    while ($true) {
        try { $last = & $Probe } catch { $last = $null }
        if ($Journal -and $Criterion) { $Journal.Observe($Criterion, $last, $null) }
        if ($null -ne $last -and (& $Until $last)) { return $last }
        # After Until, not before: a component that exits having already satisfied the condition
        # did its job, and the import step is exactly that shape.
        if ($Component) { Assert-L2ComponentAlive -Component $Component -Description $Description }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $seen = if ($null -eq $last) { '(nothing)' } else { ($last | ConvertTo-Json -Compress -Depth 6) }
            throw "Timed out after ${TimeoutSeconds}s waiting for: $Description. Last observed: $seen"
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}

<#
Throws when a component has already exited, quoting its stderr.

This exists because of how expensive the alternative was. On 2026-09-03 the first CI run of the
synthetic scenarios failed three times with

    Timed out after 120s waiting for: ControlServer is listening. Last observed: (nothing)

which is 120 wasted seconds per scenario and says nothing about the cause. The server had died on
startup two seconds in, and its stderr named the reason exactly -- a missing external secret. That
diagnosis needed the evidence artifact downloaded and opened; it should have been the first line of
the failure.
#>
function Assert-L2ComponentAlive {
    param(
        [Parameter(Mandatory)][object]$Component,
        [Parameter(Mandatory)][string]$Description
    )

    if (-not $Component.Process.HasExited) { return }

    # An unhandled startup exception lands in stderr, and it is the whole diagnosis: a validation
    # failure, a port already bound, a missing secret. Some components write nothing there and die
    # with a bare exit code, so the message has to stand on its own without the tail.
    $detail = ''
    if ($Component.ErrLog -and (Test-Path -LiteralPath $Component.ErrLog)) {
        $tail = @(Get-Content -LiteralPath $Component.ErrLog -Tail 20 -ErrorAction SilentlyContinue)
        if ($tail.Count -gt 0) {
            $detail = "`n--- $($Component.Name) stderr, last $($tail.Count) line(s) ---`n" +
                ($tail -join "`n")
        }
    }
    throw ("Component '$($Component.Name)' exited with code $($Component.Process.ExitCode) while " +
        "waiting for: $Description.$detail")
}

<#
Waits until the journey runtime has come round at least $Count more times, and returns the count it
reached.

This is what a scenario waits on before asserting that something did *not* happen. JourneyRuntimeEngine
reads the RIoT Map station catalog first thing on every iteration -- including the iterations where a
Blocked journey makes it do nothing else -- so the fake RIoT's mapStationReads is the one observable
that says "the runtime had another chance". Without it, "the vehicle took no further demand" could
only be written as a sleep, and a sleep is the thing that turns a slow machine into a flaky one.

The count is incremented when the request arrives, so $Count guarantees that many iterations have
*started* and at least $Count - 1 have finished. Ask for one more than the assertion needs; counting
completions is not available, because the engine does the rest of its work after this read returns.
#>
function Wait-L2Iterations {
    param(
        [Parameter(Mandatory)][object]$Riot,
        [int]$Count = 3,
        [int]$TimeoutSeconds = 60,
        [L2Journal]$Journal
    )

    $target = [long]$Riot.Snapshot().body.mapStationReads + $Count
    return Wait-L2Condition -Description "the journey runtime came round $Count more times" `
        -Journal $Journal -Criterion 'runtime-iterations' -TimeoutSeconds $TimeoutSeconds `
        -Probe { [long]$Riot.Snapshot().body.mapStationReads } `
        -Until { param($v) $v -ge $target }
}

# --- control-plane client -----------------------------------------------------------------------

<#
One client for every double, because they all speak the same dialect: runId scoping, commandId
idempotency, expectedRevision optimistic concurrency. The runId is read fresh from the double
rather than cached, so a scenario that resets a double mid-run does not have to re-plumb it.
#>
class L2Double {
    [string]$Name
    [string]$BaseUrl
    # The three fakes serve their control plane under control/v1; the real slots simulator serves
    # the same dialect under api/v1. That is the only difference, so it is a field rather than a
    # second client.
    [string]$Prefix = 'control/v1'
    # The fakes treat expectedRevision as optional; the simulator's contract makes it mandatory on
    # every mutating command. Sending it means a command can lose a race with the physical model
    # (a lock-feedback delay landing between the snapshot and the write), which is what the retry
    # below is for.
    [bool]$RequireExpectedRevision = $false

    L2Double([string]$name, [string]$baseUrl) {
        $this.Name = $name
        $this.BaseUrl = $baseUrl.TrimEnd('/')
    }

    L2Double([string]$name, [string]$baseUrl, [string]$prefix, [bool]$requireExpectedRevision) {
        $this.Name = $name
        $this.BaseUrl = $baseUrl.TrimEnd('/')
        $this.Prefix = $prefix.Trim('/')
        $this.RequireExpectedRevision = $requireExpectedRevision
    }

    [object] Snapshot() {
        return Invoke-RestMethod -Uri "$($this.BaseUrl)/$($this.Prefix)/snapshot" -TimeoutSec 10
    }

    [object] Health() {
        return Invoke-RestMethod -Uri "$($this.BaseUrl)/$($this.Prefix)/health" -TimeoutSec 10
    }

    [object] Command([string]$method, [string]$path, [hashtable]$body) {
        $payload = @{} + $body
        if (-not $payload.ContainsKey('commandId')) {
            $payload['commandId'] = [guid]::NewGuid().ToString('N')
        }
        $uri = "$($this.BaseUrl)/$($this.Prefix)/$($path.TrimStart('/'))"
        # Every 409 is retried with a fresh runId and revision, deliberately without reading the
        # reasonCode. A REVISION_CONFLICT or RUN_ID_MISMATCH means the command was rejected without
        # being executed, so a retry is the contract's own recovery; a COMMAND_ID_CONFLICT is
        # refused identically each time and just costs a few round trips before the same error
        # surfaces. What makes all three safe is keeping the commandId: the content fingerprint
        # excludes runId/commandId/expectedRevision, so a retry is the same command, not a new one.
        $attempts = if ($this.RequireExpectedRevision) { 5 } else { 1 }
        for ($attempt = 1; ; $attempt++) {
            $snapshot = $this.Snapshot()
            $payload['runId'] = $snapshot.runId
            if ($this.RequireExpectedRevision) { $payload['expectedRevision'] = $snapshot.revision }
            try {
                return Invoke-RestMethod -Uri $uri -Method $method -ContentType 'application/json' `
                    -Body ($payload | ConvertTo-Json -Depth 8) -TimeoutSec 30
            } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                if ($attempt -ge $attempts -or $_.Exception.Response.StatusCode -ne 409) { throw }
            }
        }
        # Unreachable: the loop either returns or throws.
        throw "Unreachable"
    }
}

function New-L2Double {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$Prefix,
        [switch]$RequireExpectedRevision
    )
    if ($Prefix -or $RequireExpectedRevision) {
        $effectivePrefix = if ($Prefix) { $Prefix } else { 'control/v1' }
        return [L2Double]::new($Name, $BaseUrl, $effectivePrefix, [bool]$RequireExpectedRevision)
    }
    return [L2Double]::new($Name, $BaseUrl)
}

# --- processes ----------------------------------------------------------------------------------

<#
Starts one component and returns a handle that carries its log paths, so a failed run leaves the
stdout and stderr of every process behind rather than only the orchestrator's own view.
#>
function Start-L2Process {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [Parameter(Mandatory)][string]$LogRoot,
        # The two WPF peers must be started visible. WindowStyle Hidden puts SW_HIDE in the
        # STARTUPINFO that WPF's first Show() honours, and a hidden window is not reliably
        # reachable through UI Automation -- the driver would find nothing and time out for a
        # reason that looks nothing like the cause.
        [switch]$Gui
    )

    $outLog = Join-Path $LogRoot "$Name.out.log"
    $errLog = Join-Path $LogRoot "$Name.err.log"
    $startArguments = @{
        FilePath               = $FilePath
        RedirectStandardOutput = $outLog
        RedirectStandardError  = $errLog
        WindowStyle            = if ($Gui) { 'Normal' } else { 'Hidden' }
        PassThru               = $true
    }
    if ($ArgumentList.Count -gt 0) { $startArguments['ArgumentList'] = $ArgumentList }
    if ($WorkingDirectory) { $startArguments['WorkingDirectory'] = $WorkingDirectory }
    if ($Environment.Count -gt 0) { $startArguments['Environment'] = $Environment }
    $process = Start-Process @startArguments
    return [pscustomobject]@{
        Name    = $Name
        Process = $process
        OutLog  = $outLog
        ErrLog  = $errLog
    }
}

<#
Stops components in reverse start order. Teardown runs from a finally block, so it must not throw:
a component that has already exited is the normal case, not an error to mask the real one with.
#>
function Stop-L2Process {
    param([Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$Handles)

    foreach ($handle in ($Handles | Where-Object { $_ } | Sort-Object -Descending { $_.Order })) {
        try {
            if (-not $handle.Process.HasExited) {
                $handle.Process.Kill($true)
                $null = $handle.Process.WaitForExit(10000)
            }
        } catch {
            Write-Warning "Could not stop $($handle.Name): $_"
        }
    }
}

# --- the two peer repositories --------------------------------------------------------------------

<#
Publishes the onboard HMI or the slots simulator from a throwaway clone, and caches the result by
commit so a re-run costs a directory copy rather than a build.

The clone is the point, not an optimization. Both repositories are read-only for agents -- their
content, including anything a build would generate -- so a `dotnet publish` may not run in their
worktree at all. Cloning to $CacheRoot leaves the source untouched and, because the clone is
checked out at an exact commit, records precisely which peer the evidence is about.

A dirty source worktree is refused rather than warned about: the clone would silently test the
committed state while the operator was looking at their edits.
#>
function Get-L2PeerPublish {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SourceRepository,
        # Relative to the repository root, so the caller names the project rather than a path into
        # a clone that does not exist yet.
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][string]$LogRoot,
        [L2Journal]$Journal
    )

    if (-not (Test-Path -LiteralPath $SourceRepository -PathType Container)) {
        throw "Peer repository not found: $SourceRepository"
    }
    $dirty = & git -C $SourceRepository status --porcelain
    if ($dirty) {
        throw ("Peer repository $Name has uncommitted changes, so a clone would test something " +
            "other than what you are looking at: $SourceRepository")
    }
    $commit = (& git -C $SourceRepository rev-parse HEAD).Trim()

    $target = Join-Path $CacheRoot "$Name-$commit"
    $publish = Join-Path $target 'publish'
    $stamp = Join-Path $target 'publish.ok'
    if (Test-Path -LiteralPath $stamp) {
        if ($Journal) { $Journal.Note("Reusing cached $Name publish for $commit.") }
        return [pscustomobject]@{ Name = $Name; Commit = $commit; Path = $publish; FromCache = $true }
    }

    if ($Journal) { $Journal.Note("Building $Name at $commit from a throwaway clone.") }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Path $target -Force
    $clone = Join-Path $target 'clone'
    $log = Join-Path $LogRoot "build-$Name.log"

    & git clone --quiet --no-hardlinks $SourceRepository $clone *>&1 | Tee-Object -FilePath $log | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not clone $Name; see $log" }
    & git -C $clone checkout --quiet --detach $commit *>&1 | Tee-Object -FilePath $log -Append | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $commit in the $Name clone; see $log" }

    # From inside the clone, so the peer's own global.json picks the SDK. `dotnet` resolves
    # global.json from the current directory, not from the project path, and a caller standing in
    # the workspace root would otherwise publish the peer with whatever SDK is newest.
    Push-Location -LiteralPath $clone
    try {
        $sdk = (& dotnet --version).Trim()
        if ($Journal) { $Journal.Note("dotnet SDK resolved for $Name publish: $sdk") }
        "dotnet SDK: $sdk" | Tee-Object -FilePath $log -Append | Out-Null
        & dotnet publish (Join-Path $clone $ProjectPath) -c Release -o $publish --nologo *>&1 |
            Tee-Object -FilePath $log -Append | Out-Null
        $publishExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($publishExit -ne 0) { throw "Publishing $Name failed; see $log" }

    Set-Content -LiteralPath $stamp -Value $commit -Encoding utf8NoBOM
    return [pscustomobject]@{ Name = $Name; Commit = $commit; Path = $publish; FromCache = $false }
}

<#
Copies a cached publish into this run's stage directory and rewrites one JSON settings file in the
copy. Nothing is ever written back into the cache, so a scenario that changes a port or a journal
path cannot leak into the next run.

Both peers read their configuration from a single file next to the executable and neither supports
environment or command-line overrides, so patching the staged copy is the only way to point them at
the L2 ports without editing a read-only repository.
#>
function New-L2PeerStage {
    param(
        [Parameter(Mandatory)][object]$Publish,
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][string]$SettingsFileName,
        [Parameter(Mandatory)][scriptblock]$Configure
    )

    $directory = Join-Path $StageRoot $Publish.Name
    Copy-Item -LiteralPath $Publish.Path -Destination $directory -Recurse -Force
    $settingsPath = Join-Path $directory $SettingsFileName
    # The onboard's appsettings.json carries // comments that its own loader rejects but tolerates
    # in the shipped file; ConvertFrom-Json would choke, so they go first.
    $raw = (Get-Content -Raw -LiteralPath $settingsPath) -replace '(?m)^\s*//.*$', ''
    $settings = $raw | ConvertFrom-Json
    & $Configure $settings
    $settings | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
    return $directory
}

# --- the onboard HMI, driven through UI Automation --------------------------------------------------

<#
Drives the real onboard WPF through UI Automation: this is the whole of landing step 5.

Two deliberate choices.

  Value and Invoke patterns, never key injection. ValuePattern.SetValue on ScanTextBox and
  InvokePattern on the 「手动提交」 button both work on an unfocused window, so the run does not
  fight the operator's keyboard and does not break when something else takes focus. Driving the
  Enter KeyBinding instead would reach ScannerSubmitCommand rather than ManualSubmitCommand; both
  land on the same WireToGateBusinessService.SubmitSublotAsync, differing only in the recorded
  input method.

  Driving only, never asserting. The one thing read back from the UI is whether input is accepted
  yet, which is a precondition for typing rather than a business fact. Everything a scenario
  concludes still comes from the server's database and the simulator's snapshot.

CanSubmit is worth being precise about: under WIRE_TO_GATE it comes from
WireToGateBusinessService.CanSubmitSublot (App.xaml.cs), not from the rule gateway, so waiting for
a rule-gateway connection would wait forever. The 「手动提交」 button additionally requires
non-empty text, which is why setting and invoking are separate steps with a wait between them.
#>
function New-L2OnboardDriver {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$ScanTextBoxAutomationId = 'ScanTextBox',
        [string]$SubmitButtonName = '手动提交',
        [string]$RecoveryButtonName = '申请恢复'
    )

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

    $driver = [pscustomobject]@{
        ProcessId               = $ProcessId
        ScanTextBoxAutomationId = $ScanTextBoxAutomationId
        SubmitButtonName        = $SubmitButtonName
        RecoveryButtonName      = $RecoveryButtonName
        Window                  = $null
    }

    # Every member is a ScriptMethod rather than a class: a PowerShell class resolves its type
    # literals when the module is parsed, which is before Add-Type has run.
    # Attaching to *a* top-level window of the process is not enough. When startup fails, App.xaml.cs
    # shows a modal MessageBox before MainWindow.Show(), so the only window belonging to the process
    # is that dialog -- and adopting it would leave every later wait timing out on a criterion that
    # says nothing about the configuration error that actually happened. So the main window is
    # identified by the control the driver needs, and the titles seen are reported when it never
    # appears.
    $driver | Add-Member -MemberType ScriptMethod -Name Attach -Value {
        param([int]$TimeoutSeconds = 60)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        $seen = [System.Collections.Generic.HashSet[string]]::new()
        while ($true) {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $this.ProcessId)
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $condition)
            foreach ($window in $windows) {
                $null = $seen.Add($window.Current.Name)
                $this.Window = $window
                if ($this.Element('AutomationId', $this.ScanTextBoxAutomationId)) {
                    return $window.Current.Name
                }
                $this.Window = $null
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                $titles = if ($seen.Count -eq 0) { '(no window at all)' } else { ($seen -join ' / ') }
                throw ("No window of pid $($this.ProcessId) carried " +
                    "'$($this.ScanTextBoxAutomationId)' within ${TimeoutSeconds}s. Windows seen: $titles.")
            }
            Start-Sleep -Milliseconds 250
        }
        throw 'Unreachable'
    }

    $driver | Add-Member -MemberType ScriptMethod -Name Element -Value {
        param([string]$By, [string]$Value)
        if (-not $this.Window) { throw 'Attach() has not run yet.' }
        $automationProperty = if ($By -eq 'AutomationId') {
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty
        } else {
            [System.Windows.Automation.AutomationElement]::NameProperty
        }
        return $this.Window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new($automationProperty, $Value))
    }

    # The text box is enabled exactly when CanSubmit is true, so this is the wait that replaces
    # "an operator noticed the prompt".
    $driver | Add-Member -MemberType ScriptMethod -Name CanSubmit -Value {
        $box = $this.Element('AutomationId', $this.ScanTextBoxAutomationId)
        if (-not $box) { return $false }
        return [bool]$box.Current.IsEnabled
    }

    $driver | Add-Member -MemberType ScriptMethod -Name SubmitReady -Value {
        $button = $this.Element('Name', $this.SubmitButtonName)
        if (-not $button) { return $false }
        return [bool]$button.Current.IsEnabled
    }

    $driver | Add-Member -MemberType ScriptMethod -Name SetSublot -Value {
        param([Parameter(Mandatory)][string]$Sublot)
        $box = $this.Element('AutomationId', $this.ScanTextBoxAutomationId)
        if (-not $box) { throw "No element with AutomationId '$($this.ScanTextBoxAutomationId)'." }
        # SetValue throws ElementNotEnabledException on a disabled box, which is the correct
        # failure: it means the scenario typed before the server asked for a sublot.
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Sublot)
    }

    $driver | Add-Member -MemberType ScriptMethod -Name ScanText -Value {
        $box = $this.Element('AutomationId', $this.ScanTextBoxAutomationId)
        if (-not $box) { return $null }
        return $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    }

    $driver | Add-Member -MemberType ScriptMethod -Name Submit -Value {
        $button = $this.Element('Name', $this.SubmitButtonName)
        if (-not $button) { throw "No button named '$($this.SubmitButtonName)'." }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    <#
    Every recovery-vector button in the WrapPanel binds both IsEnabled and Visibility to the same
    Can... property, so when the operator may not take that action the button is not in the
    automation tree at all. Absent and disabled therefore mean the same thing, and both read as
    "no entry for that action".

    Generic rather than one member per button because the panel carries six of them
    (申请恢复 / 取消装货 / 补偿清空 / 修正装货 / 故障交接 / 重新上报结果), and a scenario that
    drives a new one should not have to change this module.
    #>
    $driver | Add-Member -MemberType ScriptMethod -Name ButtonEnabled -Value {
        param([Parameter(Mandatory)][string]$Name)
        $button = $this.Element('Name', $Name)
        if (-not $button) { return $false }
        return [bool]$button.Current.IsEnabled
    }

    $driver | Add-Member -MemberType ScriptMethod -Name InvokeButton -Value {
        param([Parameter(Mandatory)][string]$Name)
        $button = $this.Element('Name', $Name)
        if (-not $button) { throw "No button named '$Name'." }
        # WPF's ButtonAutomationPeer.Invoke posts the click with Dispatcher.BeginInvoke, so this
        # returns even though the handler goes straight into a modal MessageBox that parks the UI
        # thread. Confirm() is what gets the thread back.
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    $driver | Add-Member -MemberType ScriptMethod -Name RecoveryAvailable -Value {
        return $this.ButtonEnabled($this.RecoveryButtonName)
    }

    $driver | Add-Member -MemberType ScriptMethod -Name RequestRecovery -Value {
        $this.InvokeButton($this.RecoveryButtonName)
    }

    <#
    Finds one of this process's modal MessageBox windows by its caption.

    **It is a descendant of the main window, not a child of the desktop root.** That is measured,
    not assumed: 2026-09-10, driving 「补偿清空」, `RootElement.FindAll(Children, pid)` returned only
    the main window while the dialog was standing open with focus on its 「No」 button, and
    `$window.FindAll(Descendants, ClassName = '#32770')` found it. The comment here used to say the
    opposite -- written from reading the code, never once run, because the only scenario that could
    have run it stops in front of the button on purpose. Six runs went into finding that out; the
    symptom was "the click does nothing", which is what a dialog you cannot see looks like.

    Both places are searched anyway. Which one holds an owned dialog is a Windows/UIA detail, and a
    driver that only knows one of them fails in a way that reads as a product defect.
    #>
    $driver | Add-Member -MemberType ScriptMethod -Name Dialog -Value {
        param([Parameter(Mandatory)][string]$Title)
        if (-not $this.Window) { throw 'Attach() has not run yet.' }
        $condition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Title))
        $owned = $this.Window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($owned) { return $owned }
        return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
    }

    <#
    Answers the modal MessageBox a recovery-vector button puts in front of the operator.

    The button is picked by AutomationId, not by caption: a MessageBox names its buttons after the
    Win32 control ids (IDYES = 6, IDNO = 7), which do not change with the display language -- and
    the language is not the one you would guess. On this Chinese Windows the captions came back as
    「Yes」/「No」, because they follow the *process* UI language, not the system's.
    #>
    $driver | Add-Member -MemberType ScriptMethod -Name Confirm -Value {
        param([Parameter(Mandatory)][string]$Title, [string]$ButtonAutomationId = '6', [int]$TimeoutSeconds = 30)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        while ($true) {
            $window = $this.Dialog($Title)
            if ($window) {
                $button = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                        $ButtonAutomationId))
                if (-not $button) { throw "Dialog '$Title' has no button with AutomationId '$ButtonAutomationId'." }
                # A dialog can be in the tree before its buttons take input. Measured 2026-09-11 on
                # real-onboard-restart-while-waiting-operator-003: pressed about a second after the
                # onboard was relaunched, Invoke threw "Operation is not valid due to the current
                # state of the object." and the request never left the vehicle. Until the deadline,
                # that is "not yet", not a failure.
                try {
                    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return $true
                } catch {
                    $cause = $_.Exception
                    while ($cause.InnerException) { $cause = $cause.InnerException }
                    if ($cause -isnot [System.InvalidOperationException] -or
                        [DateTimeOffset]::UtcNow -ge $deadline) {
                        throw
                    }
                }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                $seen = @()
                $anyDialog = [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
                foreach ($found in $this.Window.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants, $anyDialog)) {
                    $seen += "$($found.Current.Name) (owned)"
                }
                foreach ($found in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                        [System.Windows.Automation.TreeScope]::Children, $anyDialog)) {
                    $seen += "$($found.Current.Name) (desktop)"
                }
                $titles = if ($seen.Count -eq 0) { '(no dialog at all)' } else { ($seen -join ' / ') }
                throw "No dialog titled '$Title' from pid $($this.ProcessId) within ${TimeoutSeconds}s. Dialogs seen: $titles."
            }
            Start-Sleep -Milliseconds 250
        }
        throw 'Unreachable'
    }

    return $driver
}

# --- ControlServer database ---------------------------------------------------------------------

<#
Opens the server's own SQLite store read-only, borrowing Microsoft.Data.Sqlite and SQLitePCLRaw
from the ControlServer build under test rather than adding a dependency of its own. Read-only and
shared-cache so reading can never block or alter the server that owns the file.
#>
function Open-L2Database {
    param(
        [Parameter(Mandatory)][string]$HostDirectory,
        [Parameter(Mandatory)][string]$DatabasePath
    )

    foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
                            'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
        $path = Join-Path $HostDirectory $assembly
        if (Test-Path -LiteralPath $path) { Add-Type -LiteralPath $path -ErrorAction SilentlyContinue }
    }
    # Batteries_V2 reports a missing type on some builds and the queries work regardless, so this
    # is deliberately swallowed rather than allowed to fail the run before it starts.
    try { [SQLitePCL.Batteries_V2]::Init() } catch { }
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        "Data Source=$DatabasePath;Mode=ReadOnly;Cache=Shared")
    $connection.Open()
    return $connection
}

function Invoke-L2Query {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$Sql
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $reader = $command.ExecuteReader()
    $rows = @()
    while ($reader.Read()) {
        $row = [ordered]@{}
        for ($index = 0; $index -lt $reader.FieldCount; $index++) {
            $row[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
        }
        $rows += [pscustomobject]$row
    }
    $reader.Close()
    $command.Dispose()
    return , $rows
}

# --- protocol schema conformance ----------------------------------------------------------------

<#
Writes what the onboard sent, as the server stored it -- ProtocolInbox.RequestJson, one record per
row -- in the input shape of tools/ControlServer.SchemaConformance, and returns how many rows there
were. This is the one place the lines of a deployed onboard package can be seen: the onboard's own
G2 checks a test build, and a package and a deployment sit between the two (#35).

Three things the result is not, and a green run must not be read as any of them:

  Not everything the onboard sent. A line the server refused -- failed validation, wrong identity,
  stale generation, unsupported type -- is never written, and the exception closes the connection
  (OnboardTcpServer). "Every stored line conforms" is not "no bad line was ever sent".

  Not a count of sends. The table is keyed by MessageId and an equivalent replay overwrites its row
  in place (RecoveryStateReport), so the number of records is not the number of lines on the wire.

  Not the original bytes for two types. SessionHello and ExceptionRecoverySessionRequested are
  stored re-serialized with the proof replaced by "[REDACTED]" (OnboardMessageProcessor). The schema
  asks only for a non-empty string there, so both still validate; ContentHash was taken from the
  original line and will not match them.

The site names the row, so a violation points at a MessageId rather than at the table.
#>
function Export-L2InboundProtocolLines {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Origin
    )

    $rows = Invoke-L2Query -Connection $Connection `
        -Sql 'SELECT MessageId, MessageType, RequestJson FROM ProtocolInbox ORDER BY ReceivedAt, MessageId'
    $records = foreach ($row in $rows) {
        [ordered]@{
            messageType = $row.MessageType
            origin      = $Origin
            site        = "ProtocolInbox[$($row.MessageId)]"
            line        = $row.RequestJson
        } | ConvertTo-Json -Compress -Depth 3
    }
    [IO.File]::WriteAllLines($Path, [string[]]@($records), [Text.UTF8Encoding]::new($false))
    return $rows.Count
}

<#
Checks protocol lines against the pinned release's JSON Schemas with the validator, vendored schemas
and known-violation list the unit suite already uses (tests/ControlServer.Tests/OutboundSchemaConformance.cs),
and returns what it found. The report lands in $ReportDirectory: schema-conformance.txt always,
schema-coverage.json unless the vendored contract itself is wrong, schema-violations.json when
anything failed.

The validator is its own process for the reason its project file gives. Its exit code is the verdict
-- 0 conforms, 1 an unfiled violation, 2 the vendored contract does not match the pinned identity --
but a zero is only a pass when lines were actually checked. A peer that never got a line in would
otherwise read as a conforming one, and that is a check scoring "saw nothing" as "fine".
#>
function Invoke-L2SchemaConformance {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$LinesPath,
        [Parameter(Mandatory)][string]$ReportDirectory
    )

    $validator = Join-Path $Repository `
        'tools/ControlServer.SchemaConformance/bin/Release/net8.0/win-x64/ControlServer.SchemaConformance.exe'
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "The schema validator is not built: $validator"
    }
    if (-not (Test-Path -LiteralPath $LinesPath -PathType Leaf)) {
        throw "No protocol lines were recorded: $LinesPath does not exist."
    }
    $null = New-Item -ItemType Directory -Path $ReportDirectory -Force
    $known = Join-Path $Repository 'tests/ControlServer.Tests/schema-known-violations.json'

    $output = @(& $validator --lines $LinesPath --report $ReportDirectory --known $known 2>&1 |
        ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllLines(
        (Join-Path $ReportDirectory 'schema-conformance.txt'), [string[]]$output, [Text.UTF8Encoding]::new($false))

    $coveragePath = Join-Path $ReportDirectory 'schema-coverage.json'
    $coverage = if (Test-Path -LiteralPath $coveragePath) {
        Get-Content -LiteralPath $coveragePath -Raw | ConvertFrom-Json -AsHashtable
    } else {
        $null
    }
    $summary = @($output | Where-Object { $_ -like 'Schema conformance*' }) | Select-Object -First 1
    return [pscustomobject]@{
        ExitCode     = $exitCode
        LinesChecked = if ($coverage) { [int]$coverage.linesChecked } else { 0 }
        Coverage     = $coverage
        Summary      = if ($summary) { $summary } else { ($output | Select-Object -Last 3) -join ' ' }
    }
}

# --- evidence -----------------------------------------------------------------------------------

class L2Assertions {
    [System.Collections.Generic.List[object]]$Items = [System.Collections.Generic.List[object]]::new()

    [void] Add([string]$id, [string]$description, [bool]$passed, [object]$expected, [object]$actual) {
        $this.Items.Add([ordered]@{
            id          = $id
            description = $description
            outcome     = if ($passed) { 'PASS' } else { 'FAIL' }
            expected    = $expected
            actual      = $actual
        })
    }

    [bool] AllPassed() {
        return -not ($this.Items | Where-Object { $_.outcome -ne 'PASS' })
    }
}

function New-L2Assertions { return [L2Assertions]::new() }

function Write-L2Evidence {
    param(
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][L2Assertions]$Assertions,
        [Parameter(Mandatory)][string]$Outcome,
        [string]$FailureReason,
        [hashtable]$Identity = @{},
        # What was actually real in this run. The caveat at the end of SUMMARY.md is the only place
        # a reader learns whether "车载端" meant a synthetic protocol peer or the shipped WPF, and
        # getting that wrong is the difference between evidence and a claim.
        [ValidateSet('SyntheticOnboard', 'RealOnboard')][string]$Rig = 'SyntheticOnboard'
    )

    $assertionsPath = Join-Path $EvidenceRoot 'assertions.json'
    $document = [ordered]@{
        schemaVersion = 1
        scenario      = $Scenario
        runId         = $RunId
        outcome       = $Outcome
        failureReason = $FailureReason
        identity      = $Identity
        assertions    = $Assertions.Items
    }
    [IO.File]::WriteAllText(
        $assertionsPath,
        ($document | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# L2 场景证据：$Scenario")
    $lines.Add('')
    $lines.Add("结论：**$Outcome**")
    if ($FailureReason) {
        $lines.Add('')
        $lines.Add("失败原因：$FailureReason")
    }
    $lines.Add('')
    $lines.Add('## 身份')
    $lines.Add('')
    $lines.Add('| 项 | 值 |')
    $lines.Add('| --- | --- |')
    $lines.Add("| runId | ``$RunId`` |")
    foreach ($key in ($Identity.Keys | Sort-Object)) {
        $value = $Identity[$key]
        if ($value -is [System.Collections.IDictionary]) {
            # A nested block (the protocol release triple) gets one row per field. Rendered whole it
            # would print the dictionary's type name, which reads like a filled-in value and is not.
            foreach ($inner in $value.Keys) {
                $lines.Add("| $key.$inner | ``$($value[$inner])`` |")
            }
        } else {
            $lines.Add("| $key | ``$value`` |")
        }
    }
    $lines.Add('')
    $lines.Add('## 判据')
    $lines.Add('')
    $lines.Add('| 判据 | 结论 | 期望 | 实际 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($item in $Assertions.Items) {
        $expected = ($item.expected | Out-String).Trim() -replace '\|', '\|' -replace '\r?\n', ' '
        $actual = ($item.actual | Out-String).Trim() -replace '\|', '\|' -replace '\r?\n', ' '
        $lines.Add("| $($item.description) | $($item.outcome) | ``$expected`` | ``$actual`` |")
    }
    $lines.Add('')
    $lines.Add('## 目录内容')
    $lines.Add('')
    $lines.Add('- `assertions.json` —— 机器可读的判据结论')
    $lines.Add('- `timeline.jsonl` —— 一行一次判据翻转，只追加')
    $lines.Add('- `logs/` —— 每个组件的 stdout 与 stderr')
    $lines.Add('- `snapshots/` —— 收尾时各控制面与服务端数据库的快照')
    $lines.Add('- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细')
    $lines.Add('')
    if ($Rig -eq 'RealOnboard') {
        $lines.Add('本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。')
        $lines.Add('条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。')
        $lines.Add('')
        $lines.Add('L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。')
        $lines.Add('见 `docs/RELEASE-CANDIDATE.md` 第 11 节。')
    } else {
        $lines.Add('L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，')
        $lines.Add('**不代表真实 RCS、真车、真实 IO 模块或接线合格**。')
    }

    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'SUMMARY.md'),
        ($lines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
}

function Get-L2DeterministicId {
    <#
    .SYNOPSIS
    服务端派生消息 id 的同一个算法，用来在判据里算出它该有的值。
    .DESCRIPTION
    与 `WireToGateStore.DeterministicGuid` 逐字节一致：SHA-256 取前 16 字节，写入 UUID 版本位 5
    与 variant 位。**判据宁可自己算，也不要把 id 写成字面量**——写死的字面量在 id 派生方式改变
    时不会报错，只会静静匹配不到。
    #>
    param([Parameter(Mandatory)][string]$Value)

    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Value))
    $guidBytes = [byte[]]$bytes[0..15]
    $guidBytes[6] = [byte](($guidBytes[6] -band 0x0f) -bor 0x50)
    $guidBytes[8] = [byte](($guidBytes[8] -band 0x3f) -bor 0x80)
    return [guid]::new($guidBytes).ToString('D')
}

function Get-L2Journey {
    <#
    .SYNOPSIS
    一个需求所属旅程的扁平视图，列名沿用 ADR-cross-0057 之前那一行的叫法。
    .DESCRIPTION
    旅程现在有自己的主键，停靠与需求各自成表（`JourneyRuntimes` / `JourneyStops` /
    `JourneyDemands`）。这个函数把三张表 join 回一行，并补上按停靠与轮次派生的
    `WorklistMessageId` 与 `SublotRequestMessageId`，于是既有判据不必改写。

    **只对「一个需求、一个取货停靠」的旅程成立。**一趟多单的场景要自己读那三张表——那正是它要
    断言的东西，用这个视图会把它压平成看不出区别。
    #>
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId
    )

    $rows = Invoke-L2Query -Connection $Connection -Sql @"
SELECT
    r.JourneyId, r.Stage, r.AgvId, r.VehicleKey, r.AgvLifecycleGeneration, r.MapId, r.MapIdentity,
    r.DispatchZone, r.GateStationId, r.GateStationRiotId, r.OperationSessionId,
    r.DispatchGeneration, r.CurrentStopSequence, r.NextStopSequence, r.HoldingStartedAt,
    r.LoadingClosedReason, r.BlockReasonCode, r.CreatedAt, r.UpdatedAt,
    d.DemandId, d.ExpectedBasketCount, d.TargetSlotsJson, d.LoadCommandMessageId,
    d.LoadSlotOperationAttemptId, d.UnloadCommandMessageId, d.UnloadSlotOperationAttemptId,
    d.ConsumedSublotMessageId, d.LoadCommandedAt, d.UnloadCommandedAt, d.State AS DemandState,
    p.Sequence AS PickupSequence, p.State AS PickupState, p.StationId AS PickupStationId,
    p.StationRiotId AS PickupStationRiotId, p.RouteEvidenceId,
    p.MovementLegId AS PickupMovementLegId, p.UpperId AS PickupUpperId,
    p.PreDepartureSafetyCheckMessageId, p.PreDepartureSafetyCheckId, p.SublotWaitStartedAt,
    p.ConsumedSafetyResultMessageId, p.WorklistRevision, p.PlanRevision,
    p.VehicleBusinessRevision, p.VehicleBusinessMessageId, p.PlanMessageId,
    p.LoadRound AS PickupLoadRound,
    g.Sequence AS GateSequence, g.State AS GateState, g.MovementLegId AS GateMovementLegId,
    g.UpperId AS GateUpperId, g.VehicleBusinessMessageId AS GateVehicleBusinessMessageId,
    g.PlanMessageId AS GatePlanMessageId, g.LoadRound AS GateLoadRound
FROM JourneyDemands d
JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId
JOIN JourneyStops p ON p.JourneyId = d.JourneyId AND p.Role = 'PICKUP'
JOIN JourneyStops g ON g.JourneyId = d.JourneyId AND g.Role = 'GATE'
WHERE d.DemandId = '$DemandId'
"@
    if ($rows.Count -eq 0) {
        return , @()
    }

    foreach ($row in $rows) {
        $round = if ($row.PickupLoadRound -gt 0) { $row.PickupLoadRound } else { 1 }
        $row | Add-Member -NotePropertyName 'WorklistMessageId' -NotePropertyValue (
            Get-L2DeterministicId -Value "$($row.JourneyId)|stop-$($row.PickupSequence)|worklist-$round")
        $row | Add-Member -NotePropertyName 'SublotRequestMessageId' -NotePropertyValue (
            Get-L2DeterministicId -Value "$($row.JourneyId)|stop-$($row.PickupSequence)|sublot-request-$round")
        $gateRound = if ($row.GateLoadRound -gt 0) { $row.GateLoadRound } else { 1 }
        $row | Add-Member -NotePropertyName 'GateWorklistMessageId' -NotePropertyValue (
            Get-L2DeterministicId -Value "$($row.JourneyId)|stop-$($row.GateSequence)|worklist-$gateRound")
    }
    return , $rows
}

Export-ModuleMember -Function New-L2Journal, Wait-L2Condition, Assert-L2ComponentAlive,
    Wait-L2Iterations, New-L2Double,
    Start-L2Process, Stop-L2Process, Open-L2Database, Invoke-L2Query, New-L2Assertions,
    Export-L2InboundProtocolLines, Invoke-L2SchemaConformance,
    Write-L2Evidence, Get-L2PeerPublish, New-L2PeerStage, New-L2OnboardDriver,
    Get-L2Journey, Get-L2DeterministicId
