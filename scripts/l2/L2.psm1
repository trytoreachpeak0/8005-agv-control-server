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

    # core.longpaths on both commands. The onboard repository commits its G2 evidence under paths like
    # evidence/g2/<run>/protocol-v1.0.0/FP-IS-00/<timestamp>-<hash>/..., and below this cache root
    # (%LOCALAPPDATA%\8005-l2-peers\onboard-hmi-<40 hex>\clone\) that runs past MAX_PATH: on
    # 2026-09-13 the clone of c86bac5 "succeeded" and its checkout failed with "Filename too long".
    # The G3 runners never hit it because they clone under a short StageRoot.
    & git -c core.longpaths=true clone --quiet --no-hardlinks $SourceRepository $clone *>&1 |
        Tee-Object -FilePath $log | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not clone $Name; see $log" }
    & git -c core.longpaths=true -C $clone checkout --quiet --detach $commit *>&1 |
        Tee-Object -FilePath $log -Append | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $commit in the $Name clone; see $log" }

    # From inside the clone, so the peer's own global.json (if it has one) picks the SDK rather than
    # whatever directory the run was launched from.
    Push-Location -LiteralPath $clone
    try {
        & dotnet publish (Join-Path $clone $ProjectPath) -c Release -o $publish --nologo *>&1 |
            Tee-Object -FilePath $log -Append | Out-Null
    } finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Name failed; see $log" }

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

    # 「申请恢复」 binds both IsEnabled and Visibility to CanRequestWireToGateRecovery, so when the
    # operator may not start a recovery the button is not in the automation tree at all. Absent and
    # disabled therefore mean the same thing here, and both read as "no recovery entry".
    $driver | Add-Member -MemberType ScriptMethod -Name RecoveryAvailable -Value {
        $button = $this.Element('Name', $this.RecoveryButtonName)
        if (-not $button) { return $false }
        return [bool]$button.Current.IsEnabled
    }

    $driver | Add-Member -MemberType ScriptMethod -Name RequestRecovery -Value {
        $button = $this.Element('Name', $this.RecoveryButtonName)
        if (-not $button) { throw "No button named '$($this.RecoveryButtonName)'." }
        # WPF's ButtonAutomationPeer.Invoke posts the click with Dispatcher.BeginInvoke, so this
        # returns even though the handler goes straight into a modal MessageBox that parks the UI
        # thread. Confirm() is what gets the thread back.
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    # The load recovery vectors -- 「取消装货」「修正装货」「补偿清空」「故障交接」 -- are bound the way
    # 「申请恢复」 is: IsEnabled and Visibility both follow the capability, so absent and disabled read
    # alike. Addressed by caption because that is what the operator is told to press.
    $driver | Add-Member -MemberType ScriptMethod -Name ButtonAvailable -Value {
        param([Parameter(Mandatory)][string]$Name)
        $button = $this.Element('Name', $Name)
        if (-not $button) { return $false }
        return [bool]$button.Current.IsEnabled
    }

    $driver | Add-Member -MemberType ScriptMethod -Name InvokeButton -Value {
        param([Parameter(Mandatory)][string]$Name)
        $button = $this.Element('Name', $Name)
        if (-not $button) { throw "No button named '$Name'." }
        # Posted, like RequestRecovery: the handler's confirmation MessageBox is answered by Confirm().
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    <#
    Every window of the process: its top-level windows, and the windows each of them owns.

    The second half is not optional. MessageBox.Show without an owner still takes the active window
    as its owner, and UI Automation files an owned window under its owner rather than under the
    desktop root. Searching the root's children alone therefore never finds the confirmation a
    recovery button raises -- which is how the first correction run, 2026-09-13, clicked 「修正装货」
    and then timed out with only the main window "seen" while the dialog sat on screen.
    #>
    $driver | Add-Member -MemberType ScriptMethod -Name Windows -Value {
        $process = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $this.ProcessId)
        $isWindow = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)
        $found = [System.Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
        foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $process)) {
            $found.Add($top)
            foreach ($owned in $top.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isWindow)) {
                $found.Add($owned)
            }
        }
        return , $found.ToArray()
    }

    # Titles of every window of the process. A refused recovery request answers with a modal
    # MessageBox (「修正装货失败」 and its siblings); a scenario names that in its evidence instead of
    # timing out on the next thing it waits for.
    $driver | Add-Member -MemberType ScriptMethod -Name WindowTitles -Value {
        return , @($this.Windows() | ForEach-Object { [string]$_.Current.Name })
    }

    <#
    Answers the modal MessageBox a recovery button puts in front of the operator. It is found among
    every window of the process (see Windows), never through the main window's controls -- the main
    window is parked in the modal loop behind it.

    The button is picked by AutomationId, not by caption: a MessageBox names its buttons after the
    Win32 control ids (IDYES = 6, IDNO = 7), which do not change with the display language, while
    the caption is 「是(Y)」 only on a Chinese Windows.
    #>
    $driver | Add-Member -MemberType ScriptMethod -Name Confirm -Value {
        param([Parameter(Mandatory)][string]$Title, [string]$ButtonAutomationId = '6', [int]$TimeoutSeconds = 30)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        $seen = [System.Collections.Generic.HashSet[string]]::new()
        while ($true) {
            foreach ($window in $this.Windows()) {
                $null = $seen.Add($window.Current.Name)
                if ($window.Current.Name -ne $Title) { continue }
                $button = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                        $ButtonAutomationId))
                if (-not $button) { throw "Dialog '$Title' has no button with AutomationId '$ButtonAutomationId'." }
                $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                return $true
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                $titles = if ($seen.Count -eq 0) { '(no window at all)' } else { ($seen -join ' / ') }
                throw "No dialog titled '$Title' from pid $($this.ProcessId) within ${TimeoutSeconds}s. Windows seen: $titles."
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

<#
Renders one identity entry as one or more table rows.

A nested value -- protocolReleaseIdentity is nine fields and a fleet is a list -- would otherwise
print as its type name, which is worse than not printing it: the SUMMARY.md is the half of the
evidence a person reads, and "System.Collections.Specialized.OrderedDictionary" in the protocol
identity row is exactly the field a reader came to check.
#>
function Format-L2IdentityRows {
    param(
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][object]$Value
    )

    if ($null -eq $Value) {
        return @("| $Name | ``(null)`` |")
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $rows = @()
        foreach ($key in $Value.Keys) {
            $rows += "| $Name.$key | ``$($Value[$key])`` |"
        }
        return $rows
    }
    if ($Value -isnot [string] -and $Value -is [System.Collections.IEnumerable]) {
        return @("| $Name | ``$(($Value | ForEach-Object { [string]$_ }) -join ', ')`` |")
    }
    return @("| $Name | ``$Value`` |")
}

function Write-L2Evidence {
    param(
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][L2Assertions]$Assertions,
        [Parameter(Mandatory)][string]$Outcome,
        [string]$FailureReason,
        # Free-form, and deliberately so: what identifies a run differs by rig and by scenario, and
        # a fixed schema here would have to be edited for every one. Specification 8.4 names the two
        # the full product requires -- protocolReleaseIdentity and batchId -- and the orchestrator
        # supplies both; nested values render as their own rows below rather than as a type name.
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
        foreach ($row in (Format-L2IdentityRows -Name $key -Value $Identity[$key])) {
            $lines.Add($row)
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

Export-ModuleMember -Function New-L2Journal, Wait-L2Condition, Assert-L2ComponentAlive,
    Wait-L2Iterations, New-L2Double,
    Start-L2Process, Stop-L2Process, Open-L2Database, Invoke-L2Query, New-L2Assertions,
    Write-L2Evidence, Format-L2IdentityRows, Get-L2PeerPublish, New-L2PeerStage, New-L2OnboardDriver
