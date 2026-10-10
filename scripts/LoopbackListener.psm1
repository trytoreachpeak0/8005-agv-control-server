#Requires -Version 7

# Loopback HttpListener for the offline self-checks. Stub with the behaviour the self-checks had before
# control-server#312: a random port in 49200-49900 and no second try.
function Start-LoopbackListener([int]$FirstPort = 0, [int]$Attempts = 5) {
    $port = if ($FirstPort -gt 0) { $FirstPort } else { Get-Random -Minimum 49200 -Maximum 49900 }
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    return [pscustomobject]@{ Listener = $listener; Port = $port; Refused = @() }
}

Export-ModuleMember -Function Start-LoopbackListener
