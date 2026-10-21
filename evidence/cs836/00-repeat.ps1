#Requires -Version 7
# Three dotnet test processes of the same test in parallel, pinned to 4 cores (inherited by testhost).
param([Parameter(Mandatory)][string]$Out, [string]$Filter = 'FullyQualifiedName~OnboardHandshakePushGateTests.APushForThePreviousSession', [int]$Processes = 3)
$self = [Diagnostics.Process]::GetCurrentProcess()
$self.ProcessorAffinity = [IntPtr]0xF0
New-Item -ItemType Directory -Force $Out | Out-Null
Set-Location C:/work/8005-wt/cs836-8005-agv-control-server
$jobs = foreach ($p in 1..$Processes) {
    $env:CS836_LOG = "$Out/server-errors-$p.txt"
    Start-Process dotnet -ArgumentList @('test', 'tests/ControlServer.Tests', '-c', 'Release', '--no-build', '--filter', $Filter) `
        -RedirectStandardOutput "$Out/run-$p.txt" -RedirectStandardError "$Out/run-$p.err" -NoNewWindow -PassThru
}
$jobs | Wait-Process
foreach ($j in $jobs) { "exit=$($j.ExitCode)" }
