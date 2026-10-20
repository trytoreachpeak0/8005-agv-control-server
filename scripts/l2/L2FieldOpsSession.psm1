#Requires -Version 7
<#
The rig's setup runs its FieldOps verbs through one `ControlServer.FieldOps.exe session` process (control-server#804).

Each verb used to start its own process: about 6 per scenario at about 2 s each, most of it process start and EF model
building, which was half of the time a scenario spent before "Environment is up" (run 39264072063: 1295 of 2566 s over
107 runs). The session runs the same verbs with the same arguments, in the same order, against the same file; each
command opens its own DbContext and the session clears the SQLite pool after it, so the database sees what it saw from
one process per command.

The session is the setup's alone. The scenario's own $Context.InvokeFieldOps still starts one process per command:
some scenarios judge FieldOps itself, and none of them changes here.

Protocol (tools/ControlServer.FieldOps/Program.Session.cs): one JSON array of strings per stdin line, the command's
arguments; the command's output, stderr merged in, then a line '##fieldops-session-exit <code>'.
#>

Set-StrictMode -Version Latest

$script:ExitMarker = '##fieldops-session-exit '

<#
Starts the session process. Its stdout is read here; its stderr is not redirected, so anything the process writes
before the session takes over its error stream (a refused start) lands in the orchestrator's own output.
#>
function Start-L2FieldOpsSession {
    param([Parameter(Mandatory)][string]$Executable)

    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.ArgumentList.Add('session')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.WorkingDirectory = Split-Path -Parent $Executable
    $process = [Diagnostics.Process]::Start($start)
    [pscustomobject]@{ Process = $process; Executable = $Executable }
}

<#
Runs one command in the session and returns @{ ExitCode; Lines }. Throws, naming the command, when the session ends
before answering or does not answer within -TimeoutSeconds: either way the setup cannot know what the command did.
#>
function Invoke-L2FieldOpsSessionCommand {
    param(
        [Parameter(Mandatory)][object]$Session,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 300
    )

    $process = $Session.Process
    $verb = $Arguments[0]
    if ($process.HasExited) {
        throw "ControlServer.FieldOps session had already exited with $($process.ExitCode) before $verb."
    }
    $process.StandardInput.WriteLine((ConvertTo-Json -InputObject @($Arguments) -Compress))
    $process.StandardInput.Flush()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lines = [Collections.Generic.List[string]]::new()
    while ($true) {
        $read = $process.StandardOutput.ReadLineAsync()
        $left = [Math]::Max(0, [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        if (-not $read.Wait($left)) {
            throw "ControlServer.FieldOps session did not answer $verb within $TimeoutSeconds s: $($lines -join "`n")"
        }
        $line = $read.Result
        if ($null -eq $line) {
            $null = $process.WaitForExit(5000)
            $code = if ($process.HasExited) { $process.ExitCode } else { '(still running)' }
            throw "ControlServer.FieldOps session ended with $code while running ${verb}: $($lines -join "`n")"
        }
        if ($line.StartsWith($script:ExitMarker, [StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                ExitCode = [int]$line.Substring($script:ExitMarker.Length)
                Lines    = @($lines)
            }
        }
        $lines.Add($line)
    }
}

<#
Ends the session: closes its stdin, which it reads as "no more commands", and waits. A session that does not end is
killed. Safe to call on a session that already ended, and on $null.
#>
function Stop-L2FieldOpsSession {
    param([object]$Session, [int]$TimeoutSeconds = 30)

    if ($null -eq $Session) { return }
    $process = $Session.Process
    try {
        if (-not $process.HasExited) {
            try { $process.StandardInput.Close() } catch [IO.IOException] { }
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $null = $process.WaitForExit(10000)
            }
        }
    } finally {
        $process.Dispose()
    }
}

<#
One command in its own process, the way the scenario's $Context.InvokeFieldOps runs it. Returns
@{ ExitCode; Output (stdout lines); Errors (stderr lines) }.

stdout is read as UTF-8, which is what FieldOps writes, whatever the console's code page (control-server#804 review S1).
`& exe 2>&1` decoded it with the console's code page instead: 437 or 1252 turned every Chinese name into '?', and 936
(vm01, zh-CN) could swallow the closing quote after a field ending in an odd number of Chinese bytes, so the JSON no
longer parsed. Not by switching [Console]::OutputEncoding around the call: the rig's lanes may share one console.
#>
function Invoke-L2FieldOpsProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $start.WorkingDirectory = Split-Path -Parent $Executable
    $process = [Diagnostics.Process]::Start($start)
    try {
        # Both pipes drained at once: a process that fills one while nobody reads it never exits.
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output   = @($output.Result -split '\r?\n' | Where-Object { $_ -ne '' })
            Errors   = @($errors.Result -split '\r?\n' | Where-Object { $_ -ne '' })
        }
    } finally {
        $process.Dispose()
    }
}

Export-ModuleMember -Function Start-L2FieldOpsSession, Invoke-L2FieldOpsSessionCommand, Stop-L2FieldOpsSession, Invoke-L2FieldOpsProcess
