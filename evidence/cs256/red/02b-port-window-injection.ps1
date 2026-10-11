#Requires -Version 7
# cs#256: reproduces evidence/cs256/red/02b-port-window-injection.txt.
# Before the fix the fixture probed a port, released it and only then let the server bind it; a contender that
# takes the port in between makes the server fail to start. After the fix a placeholder bound to 0.0.0.0, never
# listening, holds the port until the server has stopped. Run: pwsh -File <this file> -Out <new file>
param(
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Out must be a new file: $Out" }

function New-TcpSocket { [System.Net.Sockets.Socket]::new('InterNetwork', 'Stream', 'Tcp') }
function Get-BindError($exception) {
    $inner = $exception.InnerException ?? $exception
    ($inner -is [System.Net.Sockets.SocketException]) ? "$($inner.SocketErrorCode): $($inner.Message)" : $inner.Message
}
$any = [System.Net.IPAddress]::Any
$loopback = [System.Net.IPAddress]::Loopback

$log = [System.Collections.Generic.List[string]]::new()
$log.Add("cs#256 port window injection, $(Get-Date -Format o), pwsh $($PSVersionTable.PSVersion), $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)")

$log.Add('--- before the fix: probe on 127.0.0.1:0, Stop(), a contender binds the port, then the server binds it')
$probe = [System.Net.Sockets.TcpListener]::new($loopback, 0); $probe.Start(); $p = $probe.LocalEndpoint.Port; $probe.Stop()
$contender = [System.Net.Sockets.TcpListener]::new($loopback, $p); $contender.Start()
$log.Add("probe released $p; contender bound it")
try {
    $server = [System.Net.Sockets.TcpListener]::new($loopback, $p); $server.Start()
    $log.Add('server bind: OK (unexpected)'); $server.Stop()
} catch { $log.Add("server bind: FAILED: $(Get-BindError $_.Exception)") }
$contender.Stop()

$log.Add('--- after the fix: placeholder bound on 0.0.0.0:0 and held; contenders try the port, then the server binds it')
$hold = New-TcpSocket; $hold.ExclusiveAddressUse = $false; $hold.Bind([System.Net.IPEndPoint]::new($any, 0)); $q = $hold.LocalEndPoint.Port
$log.Add("placeholder holds $q")
$s = New-TcpSocket
try { $s.ExclusiveAddressUse = $true; $s.Bind([System.Net.IPEndPoint]::new($any, $q)); $log.Add("contender (exclusive wildcard) bound $q (unexpected)") }
catch { $log.Add("contender (exclusive wildcard): refused: $(Get-BindError $_.Exception)") } finally { $s.Dispose() }
$s = New-TcpSocket
try { $s.Bind([System.Net.IPEndPoint]::new($loopback, 0)); $log.Add("contender (asks the system for a port on 127.0.0.1) was given $($s.LocalEndPoint.Port); reserved is $q; equal would be a failure: $($s.LocalEndPoint.Port -eq $q)") }
finally { $s.Dispose() }
try {
    $server = [System.Net.Sockets.TcpListener]::new($loopback, $q); $server.Start()
    $client = [System.Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1', $q); $accepted = $server.AcceptTcpClient()
    $log.Add('server bind: OK; a loopback client connected and the server accepted it')
    $accepted.Dispose(); $client.Dispose(); $server.Stop()
} catch { $log.Add("server bind: FAILED: $(Get-BindError $_.Exception)") }
$hold.Dispose()

$log.Add('--- limit (independent review S1): a process that binds the port number on 127.0.0.1 directly is not stopped')
$hold = New-TcpSocket; $hold.ExclusiveAddressUse = $false; $hold.Bind([System.Net.IPEndPoint]::new($any, 0)); $r = $hold.LocalEndPoint.Port
$log.Add("placeholder holds $r")
$direct = New-TcpSocket
try { $direct.Bind([System.Net.IPEndPoint]::new($loopback, $r)); $log.Add("direct bind on 127.0.0.1:${r}: OK (the placeholder does not stop it)") }
catch { $log.Add("direct bind on 127.0.0.1:${r}: refused: $(Get-BindError $_.Exception)") }
try {
    $server = [System.Net.Sockets.TcpListener]::new($loopback, $r); $server.Start()
    $log.Add('server bind after the direct bind: OK'); $server.Stop()
} catch { $log.Add("server bind after the direct bind: FAILED: $(Get-BindError $_.Exception)") }
$direct.Dispose(); $hold.Dispose()

$log | Set-Content -Path $Out
$log
