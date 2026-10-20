#Requires -Version 7
<#
cs#812, question from the #805 review: does the Host's redirected output depend on whether FieldOps (which sets
Console.OutputEncoding = UTF-8, i.e. SetConsoleOutputCP(65001) on the console it shares) ran first?

Run inside a console of its own (the launcher below gives it one). For one Host build, four runs, each holding the
database lock so the Host logs the database path (under a directory named 关卡) and waits:
  L2-before / L2-after       Start-L2Process, exactly as the L2 harness starts the server
  shared-before / shared-after  Start-Process -NoNewWindow: the Host shares this console (worst case)
'after' = after one FieldOps run in this console. Reports the console output code page around FieldOps and the bytes
the Host wrote for 关卡.
#>
param(
    [Parameter(Mandatory)][string]$HostExe,
    [Parameter(Mandatory)][string]$FieldOpsExe,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Repository 'scripts/l2/L2.psm1') -Force
Add-Type -Namespace Cs812 -Name Kernel32 -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern uint GetConsoleOutputCP();
'@
$null = New-Item -ItemType Directory -Force -Path $OutDir

function Read-Shared([string]$Path) {
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { $memory = [IO.MemoryStream]::new(); $stream.CopyTo($memory); , $memory.ToArray() } finally { $stream.Dispose() }
}

function Invoke-HostRun([string]$Label, [switch]$Shared) {
    $dir = Join-Path $OutDir $Label
    $db = Join-Path (Join-Path $dir '关卡') 'controlserver.db'
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $db)
    $lock = [IO.File]::Open("$db.instance-lock", 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $environment = @{
            ConnectionStrings__ControlServer = "Data Source=$db"
            Health__url = 'http://127.0.0.1:48899'; OnboardTransport__listenAddress = '127.0.0.1'; OnboardTransport__port = '48898'
            RIoT__baseUrl = 'http://127.0.0.1:48897'; MesIngest__baseUrl = 'http://127.0.0.1:48896'; JourneyRuntime__enabled = 'false'
        }
        if ($Shared) {
            $out = Join-Path $dir 'control-server.out.log'
            $process = Start-Process -FilePath $HostExe -WorkingDirectory (Split-Path -Parent $HostExe) -NoNewWindow -PassThru `
                -RedirectStandardOutput $out -RedirectStandardError (Join-Path $dir 'control-server.err.log') -Environment $environment
        } else {
            $started = Start-L2Process -Name 'control-server' -FilePath $HostExe -WorkingDirectory (Split-Path -Parent $HostExe) `
                -Environment $environment -LogRoot $dir
            $process = $started.Process; $out = $started.OutLog
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        $seen = $false
        while (-not $seen -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
            $seen = (Test-Path $out) -and ([Text.Encoding]::ASCII.GetString((Read-Shared $out)) -match 'holds the lock on database')
        }
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit()
        if (-not $seen) { throw "$Label : the host never logged the lock wait within 8 s (it would take the lock at 10 s)." }
    } finally { $lock.Dispose() }
    $bytes = Read-Shared $out
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    $at = $text.IndexOf('controlserver.db')
    [ordered]@{
        run = $Label
        consoleOutputCodePageOfThisConsole = [Cs812.Kernel32]::GetConsoleOutputCP()
        bytesBeforeFileName = ($at -ge 8) ? [Convert]::ToHexString($bytes, $at - 8, 8) : '(line not found)'
        utf8Gate = ([Convert]::ToHexString($bytes)).Contains('E585B3E58DA1')
    }
}

$runs = @()
$runs += Invoke-HostRun 'L2-before'
$runs += Invoke-HostRun 'shared-before' -Shared
$cpBefore = [Cs812.Kernel32]::GetConsoleOutputCP()
& $FieldOpsExe *> (Join-Path $OutDir 'fieldops.txt')
$cpAfter = [Cs812.Kernel32]::GetConsoleOutputCP()
$runs += Invoke-HostRun 'L2-after'
$runs += Invoke-HostRun 'shared-after' -Shared
[ordered]@{ hostExe = $HostExe; consoleCodePageBeforeFieldOps = $cpBefore; consoleCodePageAfterFieldOps = $cpAfter; runs = $runs } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultPath -Encoding utf8NoBOM
