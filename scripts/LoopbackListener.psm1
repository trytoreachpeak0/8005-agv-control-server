#Requires -Version 7

# A loopback HttpListener on a port that is free, not a random one, for the offline self-checks
# (Test-StagedG3ErrorPath.ps1, l2/Test-L2DoubleCommandError.ps1).
#
# A random pick in 49200-49900 sits in Windows' ephemeral range and collides with outbound connections the
# other runners on the same guest hold: on 2026-09-22 Test-L2DoubleCommandError.ps1, written that way, went
# red in CI with HttpListener.Start() "The process cannot access the file because it is being used by another
# process." (ERROR_SHARING_VIOLATION, 32), and again in run 38047772887 (control-server#312). So: ask the
# system for a free port (bind 0, read it, release it), and if something takes it in the moment between,
# take another one, a bounded number of times. -FirstPort exists for the self-checks that make the retry
# happen on purpose.
function Start-LoopbackListener([int]$FirstPort = 0, [int]$Attempts = 5) {
    $refused = [System.Collections.Generic.List[int]]::new()
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $port = if ($attempt -eq 0 -and $FirstPort -gt 0) { $FirstPort } else {
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $probe.Start()
            try { $probe.LocalEndpoint.Port } finally { $probe.Stop() }
        }
        $candidate = [System.Net.HttpListener]::new()
        $candidate.Prefixes.Add("http://localhost:$port/")
        try {
            $candidate.Start()
            return [pscustomobject]@{ Listener = $candidate; Port = $port; Refused = @($refused) }
        } catch {
            $candidate.Close()
            $base = $_.Exception.GetBaseException()
            # Only a port someone else holds is worth another try; anything else is a real failure.
            if (-not ($base -is [System.Net.HttpListenerException] -and $base.ErrorCode -eq 32)) { throw }
            $refused.Add($port)
        }
    }
    throw "No loopback port could be listened on in $Attempts attempts; refused: $($refused -join ', ')."
}

Export-ModuleMember -Function Start-LoopbackListener
