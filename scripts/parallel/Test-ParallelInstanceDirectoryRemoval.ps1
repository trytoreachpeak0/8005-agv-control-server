#Requires -Version 7

<#
.SYNOPSIS
    Self-test for control-server#715: deleting an instance directory waits for the processes that
    held it and retries, briefly, while a file in it is still in use.

.DESCRIPTION
    The cutover trial's first install (2026-10-13 17:44) stopped half-way: the installer stopped the
    retired FakeMesIngest and deleted its directory at once, and Remove-Item failed with
    "Access to the path '...\clrjit.dll' is denied." -- the process was gone, the image mapping was
    not yet. Every case here uses a real handle on a real file in a temporary directory, held by this
    process or by a child pwsh, never a service and never a path on a server:

      * a child holding a file open, or holding a copied DLL mapped (the clrjit.dll shape), that lets
        go a moment later: the delete succeeds after retrying;
      * the installer's call-site sequence (Stop-Process, Wait-ParallelInstanceProcessExit,
        Remove-ParallelInstanceDirectory) against a child that would otherwise hold the DLL for a minute;
      * a handle that is never released: the retries run out and the error names the file and the
        process holding it;
      * a refusal is not retried, and the path is re-checked before every retry;
      * a process that does not exit in time is an error naming its id, not a silent continue.

    The temporary directory is only deletable because the test adds its parent to the module's
    allowlist ($script:AllowedParents) for the duration, and puts it back in a finally. Run on its own
    in seconds; Test-ParallelInstance.ps1 runs it as one of its cases, which is how CI runs it.

    No Pester, for the reason Test-ParallelInstance.ps1 gives.

.EXAMPLE
    pwsh -File scripts/parallel/Test-ParallelInstanceDirectoryRemoval.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
$module = Get-Module ParallelInstance

$script:Failed = 0
$script:Passed = 0

function Write-Result {
    param([bool] $Ok, [string] $Name, [string] $Detail)
    if ($Ok) {
        $script:Passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } else {
        $script:Failed++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $Detail" -ForegroundColor Red
    }
}

function Invoke-Case([string] $Name, [scriptblock] $Body) {
    try {
        $verdict = & $Body
        Write-Result -Ok ([bool] $verdict.Ok) -Name $Name -Detail ([string] $verdict.Detail)
    } catch {
        Write-Result -Ok $false -Name $Name -Detail "threw: $($_.Exception.Message)"
    }
}

# The pause between attempts, replaced per case: counting, sleeping for real, or changing the world
# between two attempts. The module calls it by name, so replacing it in the module's scope is enough.
$originalWait = & $module { ${function:Wait-ParallelInstanceRemovalRetry} }
$script:WaitCount = 0
function Set-RetryWait([scriptblock] $Body) {
    $script:WaitCount = 0
    & $module { param($b) Set-Item -LiteralPath function:script:Wait-ParallelInstanceRemovalRetry -Value $b } $Body
}
function Reset-RetryWait {
    if ($null -ne $originalWait) {
        & $module { param($b) Set-Item -LiteralPath function:script:Wait-ParallelInstanceRemovalRetry -Value $b } $originalWait
    }
}

# A temporary root the allowlist accepts while the test runs. GetFullPath, and a refusal if it is not
# canonical, so a machine whose temp path is an 8.3 short name fails loudly instead of testing nothing.
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "cs715-$([guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Path $root | Out-Null
$originalParents = & $module { $script:AllowedParents }
function Set-RootAllowed([bool] $Allowed) {
    $parents = $Allowed ? @($originalParents) + $root : @($originalParents)
    & $module { param($p) $script:AllowedParents = $p } $parents
}

$pwshPath = (Get-Process -Id $PID).Path
$sampleDll = Join-Path $PSHOME 'Microsoft.PowerShell.Commands.Utility.dll'
$children = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function New-InstanceDirectory([string] $Name) {
    $directory = Join-Path $root $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    Set-Content -LiteralPath (Join-Path $directory 'held.bin') -Value 'x'
    Copy-Item -LiteralPath $sampleDll -Destination (Join-Path $directory 'mapped.dll')
    New-Item -ItemType Directory -Path (Join-Path $directory 'sub') | Out-Null
    Set-Content -LiteralPath (Join-Path $directory 'sub\free.txt') -Value 'y'
    return $directory
}

function Start-Holder {
    <#
        A child pwsh that takes a handle on a file in $Directory, signals, holds it for $HoldMilliseconds,
        and exits. -Kind File: opened with FileShare.None (the "being used by another process" error).
        -Kind Dll: loaded with Assembly.LoadFile, which maps the image -- the clrjit.dll shape, and the
        "Access to the path is denied" error.
    #>
    param([string] $Directory, [ValidateSet('File', 'Dll')][string] $Kind, [int] $HoldMilliseconds)
    $ready = Join-Path $root "ready-$([guid]::NewGuid().ToString('N'))"
    $take = ($Kind -eq 'File') ?
        "`$h = [IO.File]::Open('$(Join-Path $Directory 'held.bin')', 'Open', 'Read', 'None')" :
        "`$h = [Reflection.Assembly]::LoadFile('$(Join-Path $Directory 'mapped.dll')')"
    $command = "$take; Set-Content -LiteralPath '$ready' -Value 1; Start-Sleep -Milliseconds $HoldMilliseconds"
    $info = [System.Diagnostics.ProcessStartInfo]::new($pwshPath)
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', $command)) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($info)
    $children.Add($process)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($process.HasExited) { throw "holder exited before taking its handle (exit $($process.ExitCode))" }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'holder did not take its handle within 30 s' }
        Start-Sleep -Milliseconds 50
    }
    return $process
}

try {
    Write-Host ''
    Write-Host 'Remove-ParallelInstanceDirectory retries while a file is in use (control-server#715)' -ForegroundColor Cyan

    foreach ($kind in @('File', 'Dll')) {
        Invoke-Case "a child holding $(($kind -eq 'File') ? 'a file open' : 'a DLL mapped (the clrjit.dll shape)') for 2.5 s: the delete retries and succeeds once it lets go" {
            Set-RootAllowed $true
            Set-RetryWait { param([int] $Milliseconds) $script:WaitCount++; Start-Sleep -Milliseconds $Milliseconds }
            try {
                $directory = New-InstanceDirectory "Held$kind.V2"
                $null = Start-Holder -Directory $directory -Kind $kind -HoldMilliseconds 2500
                $message = $null
                try { Remove-ParallelInstanceDirectory -Path $directory } catch { $message = $_.Exception.Message }
                @{ Ok = ($null -eq $message -and -not (Test-Path -LiteralPath $directory) -and $script:WaitCount -ge 1)
                    Detail = "threw: $message; directory left=$(Test-Path -LiteralPath $directory); waits=$($script:WaitCount)" }
            } finally { Reset-RetryWait; Set-RootAllowed $false }
        }
    }

    Invoke-Case 'the installer sequence: Stop-Process, Wait-ParallelInstanceProcessExit, then the delete succeeds with the DLL just unmapped' {
        Set-RootAllowed $true
        try {
            $directory = New-InstanceDirectory 'CallSite.V2'
            $holder = Start-Holder -Directory $directory -Kind Dll -HoldMilliseconds 60000
            $holder | Stop-Process -Force -ErrorAction SilentlyContinue
            Wait-ParallelInstanceProcessExit -Process @($holder) -TimeoutSeconds 30
            $message = $null
            try { Remove-ParallelInstanceDirectory -Path $directory } catch { $message = $_.Exception.Message }
            @{ Ok = ($null -eq $message -and -not (Test-Path -LiteralPath $directory) -and $holder.HasExited)
                Detail = "threw: $message; directory left=$(Test-Path -LiteralPath $directory); holder exited=$($holder.HasExited)" }
        } finally { Set-RootAllowed $false }
    }

    Invoke-Case 'a handle never released: every attempt is made, then the error names the file and the process holding it' {
        Set-RootAllowed $true
        Set-RetryWait { param([int] $Milliseconds) $script:WaitCount++ }
        $directory = New-InstanceDirectory 'NeverReleased.V2'
        $stream = [IO.File]::Open((Join-Path $directory 'held.bin'), 'Open', 'Read', 'None')
        try {
            $attempts = & $module { $script:DirectoryRemovalAttempts }
            $message = $null
            try { Remove-ParallelInstanceDirectory -Path $directory } catch { $message = $_.Exception.Message }
            Write-Host "        error text: $message"
            @{ Ok = ($null -ne $message -and $message.Contains((Join-Path $directory 'held.bin')) -and $message.Contains("pid $PID") -and
                    $message.Contains("after $attempts attempts") -and $attempts -eq 10 -and $script:WaitCount -eq ($attempts - 1) -and
                    (Test-Path -LiteralPath (Join-Path $directory 'held.bin')))
                Detail = "attempts=$attempts waits=$($script:WaitCount); threw: $message" }
        } finally { $stream.Dispose(); Reset-RetryWait; Set-RootAllowed $false }
    }

    Invoke-Case 'a refusal is not retried: a path outside the allowlist throws at once, no wait' {
        Set-RetryWait { param([int] $Milliseconds) $script:WaitCount++ }
        $directory = New-InstanceDirectory 'NotAllowed.V2'
        $stream = [IO.File]::Open((Join-Path $directory 'held.bin'), 'Open', 'Read', 'None')
        try {
            $message = $null
            try { Remove-ParallelInstanceDirectory -Path $directory } catch { $message = $_.Exception.Message }
            @{ Ok = ($null -ne $message -and $message.Contains('is not directly under one of') -and $script:WaitCount -eq 0 -and (Test-Path -LiteralPath $directory))
                Detail = "waits=$($script:WaitCount); threw: $message" }
        } finally { $stream.Dispose(); Reset-RetryWait }
    }

    Invoke-Case 'the path is re-checked before every retry: refused after the first attempt, nothing more is deleted' {
        Set-RootAllowed $true
        $directory = New-InstanceDirectory 'RecheckedEachTime.V2'
        $stream = [IO.File]::Open((Join-Path $directory 'held.bin'), 'Open', 'Read', 'None')
        # Between the first and the second attempt the handle goes away AND the path stops being allowed.
        # Re-checked, the second attempt refuses; not re-checked, it deletes the directory.
        Set-RetryWait { param([int] $Milliseconds) $script:WaitCount++; $stream.Dispose(); Set-RootAllowed $false }
        try {
            $message = $null
            try { Remove-ParallelInstanceDirectory -Path $directory } catch { $message = $_.Exception.Message }
            @{ Ok = ($null -ne $message -and $message.Contains('is not directly under one of') -and $script:WaitCount -eq 1 -and
                    (Test-Path -LiteralPath (Join-Path $directory 'held.bin')))
                Detail = "waits=$($script:WaitCount); held.bin left=$(Test-Path -LiteralPath (Join-Path $directory 'held.bin')); threw: $message" }
        } finally { $stream.Dispose(); Reset-RetryWait; Set-RootAllowed $false }
    }

    Write-Host ''
    Write-Host 'The error once the attempts run out names the entry and does not call a permission problem "in use" (review of #762)' -ForegroundColor Cyan
    function Get-FailureText($Exception, [string] $Target, [string] $Directory) {
        $record = [Management.Automation.ErrorRecord]::new($Exception, 'RemoveItemIOError', 'WriteError', $Target)
        return & $module { param($p, $r) Format-ParallelInstanceRemovalFailure -Path $p -ErrorRecord $r -Attempts 10 } $Directory $record
    }
    Invoke-Case 'access denied, nobody holds the file: reported as a likely permission or attribute problem, with the file name' {
        $directory = New-InstanceDirectory 'DeniedFree.V2'
        $file = Join-Path $directory 'held.bin'
        $text = Get-FailureText ([UnauthorizedAccessException]::new("Access to the path '$file' is denied.")) $file $directory
        @{ Ok = ($text.Contains("access to '$file' is denied and no process holds it") -and $text.Contains('permission or attribute') -and
                -not $text.Contains("' is still in use")); Detail = $text }
    }
    Invoke-Case 'access denied, a process holds the file: still reported as in use, with the holder' {
        $directory = New-InstanceDirectory 'DeniedHeld.V2'
        $file = Join-Path $directory 'held.bin'
        $stream = [IO.File]::Open($file, 'Open', 'Read', 'None')
        try {
            $text = Get-FailureText ([UnauthorizedAccessException]::new("Access to the path '$file' is denied.")) $file $directory
            @{ Ok = ($text.Contains("'$file' is still in use") -and $text.Contains("pid $PID") -and -not $text.Contains('permission')); Detail = $text }
        } finally { $stream.Dispose() }
    }
    Invoke-Case 'a delete-pending file (it no longer passes Test-Path) is still named in the sentence' {
        $directory = New-InstanceDirectory 'Pending.V2'
        $file = Join-Path $directory 'gone-but-pending.bin'
        $text = Get-FailureText ([IO.IOException]::new('x', [int] 0x80070020)) $file $directory
        @{ Ok = $text.Contains("'$file' is still in use"); Detail = $text }
    }
    Invoke-Case 'directory not empty (145): the directory is named with what is still in it' {
        $directory = New-InstanceDirectory 'NotEmpty.V2'
        foreach ($i in 1..7) { Set-Content -LiteralPath (Join-Path $directory "extra$i.txt") -Value 'z' }
        $text = Get-FailureText ([IO.IOException]::new('The directory is not empty.', [int] 0x80070091)) $directory $directory
        @{ Ok = ($text.Contains("'$directory' is not empty (still in it: ") -and $text.Contains('extra1.txt') -and $text.Contains('and 5 more')); Detail = $text }
    }

    Write-Host ''
    Write-Host 'Which errors are retried: in use or access denied, nothing else' -ForegroundColor Cyan
    $classified = [ordered]@{
        'IOException sharing violation (0x80070020)'          = @{ E = { $e = [IO.IOException]::new('x', [int] 0x80070020); $e }; Retry = $true }
        'IOException lock violation (0x80070021)'             = @{ E = { [IO.IOException]::new('x', [int] 0x80070021) }; Retry = $true }
        'IOException directory not empty (0x80070091)'        = @{ E = { [IO.IOException]::new('x', [int] 0x80070091) }; Retry = $true }
        'UnauthorizedAccessException (access denied)'         = @{ E = { [UnauthorizedAccessException]::new('x') }; Retry = $true }
        'the same, wrapped as an inner exception'             = @{ E = { [Management.Automation.RuntimeException]::new('outer', [UnauthorizedAccessException]::new('x')) }; Retry = $true }
        'IOException with no Win32 code'                      = @{ E = { [IO.IOException]::new('x') }; Retry = $false }
        'IOException disk full (0x80070070)'                  = @{ E = { [IO.IOException]::new('x', [int] 0x80070070) }; Retry = $false }
        'PathTooLongException'                                = @{ E = { [IO.PathTooLongException]::new('x') }; Retry = $false }
        'DirectoryNotFoundException'                          = @{ E = { [IO.DirectoryNotFoundException]::new('x') }; Retry = $false }
        'RuntimeException (a refusal)'                        = @{ E = { [Management.Automation.RuntimeException]::new("Refusing to delete 'x'") }; Retry = $false }
    }
    foreach ($label in $classified.Keys) {
        Invoke-Case "retried=$($classified[$label].Retry): $label" {
            $exception = & $classified[$label].E
            $got = & $module { param($e) Test-ParallelInstanceRemovalRetryable -Exception $e } $exception
            @{ Ok = ($got -eq $classified[$label].Retry); Detail = "got $got" }
        }
    }

    Write-Host ''
    Write-Host 'Wait-ParallelInstanceProcessExit: an exit, or an error naming the process' -ForegroundColor Cyan
    Invoke-Case 'a process that does not exit within the timeout is an error naming its id' {
        $directory = New-InstanceDirectory 'Lingering.V2'
        $holder = Start-Holder -Directory $directory -Kind File -HoldMilliseconds 60000
        $message = $null
        try { Wait-ParallelInstanceProcessExit -Process @($holder) -TimeoutSeconds 1 } catch { $message = $_.Exception.Message }
        @{ Ok = ($null -ne $message -and $message.Contains("$($holder.Id)") -and -not $holder.HasExited); Detail = "threw: $message" }
    }
    Invoke-Case 'processes that exit (or already have, or none at all) return without an error' {
        $directory = New-InstanceDirectory 'Exiting.V2'
        $holder = Start-Holder -Directory $directory -Kind File -HoldMilliseconds 500
        Wait-ParallelInstanceProcessExit -Process @($holder) -TimeoutSeconds 30
        Wait-ParallelInstanceProcessExit -Process @($holder) -TimeoutSeconds 1
        Wait-ParallelInstanceProcessExit -Process @() -TimeoutSeconds 1
        @{ Ok = $holder.HasExited; Detail = "exited=$($holder.HasExited)" }
    }

    Write-Host ''
    Write-Host 'Both installer call sites wait before deleting' -ForegroundColor Cyan
    $installer = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-ParallelInstanceLocal.ps1'))
    $sites = [ordered]@{
        'retiring the previous FakeMesIngest' = @{ From = "Write-Step `"Retiring the previous install's FakeMesIngest"; To = 'Remove-ParallelInstanceDirectory -Path $retired.FakeInstallRoot' }
        'redeploying the FakeMesIngest'       = @{ From = 'function Install-FakeMesIngest'; To = 'Remove-ParallelInstanceDirectory -Path $fakeInstallRoot' }
    }
    foreach ($site in $sites.Keys) {
        Invoke-Case "installer, $site`: handles pinned, Stop-Process, then Wait-ParallelInstanceProcessExit (no Wait-Process), then the delete" {
            $from = $installer.IndexOf($sites[$site].From)
            $to = ($from -ge 0) ? $installer.IndexOf($sites[$site].To, $from) : -1
            $segment = ($from -ge 0 -and $to -gt $from) ? $installer.Substring($from, $to - $from) : ''
            $pin = $segment.IndexOf('$null = $_.Handle')
            $stop = $segment.IndexOf('Stop-Process -Force')
            $wait = $segment.IndexOf('Wait-ParallelInstanceProcessExit')
            @{ Ok = ($pin -ge 0 -and $stop -gt $pin -and $wait -gt $stop -and -not $segment.Contains('Wait-Process'))
                Detail = "segment $from..$to; handle pinned at $pin, Stop-Process at $stop, Wait-ParallelInstanceProcessExit at $wait, Wait-Process present=$($segment.Contains('Wait-Process'))" }
        }
    }
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill(); $null = $child.WaitForExit(10000) }
    }
    Reset-RetryWait
    & $module { param($p) $script:AllowedParents = $p } $originalParents
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) -ForegroundColor ($script:Failed -eq 0 ? 'Green' : 'Red')
exit ($script:Failed -eq 0 ? 0 : 1)
