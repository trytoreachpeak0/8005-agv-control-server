#Requires -Version 7
<#
.SYNOPSIS
    Modbus TCP primitives for the W1 slot IO probe: read the module's output and input image, fire one
    unlock output, and follow the image while a person operates the slot.

.DESCRIPTION
    Plain functions, not a module. Invoke-W1SlotIoProbe.ps1 dot-sources this file when the module is
    reachable from the machine it runs on (a rehearsal against the slots simulator), and sends this
    file's text over ssh when the module sits on a vehicle's own LAN -- the HMI machine is the only
    place the real module can be reached from. Nothing here may need a module scope, the file stays
    ASCII so the text survives -EncodedCommand unchanged, and '#' appears only in comments so the
    sender can strip them.

    It speaks the subset the onboard client speaks (SQCD.Agv.Infrastructure.ModbusTcpIoModuleClient):
    FC01 for the unlock outputs, FC02 for lock feedback and light curtain inputs, FC05 0xFF00 to fire
    an unlock. The module's pulse mode clears the output by itself. FC05 0x0000 is sent only when it
    has not, and as soon as the deadline passes, because a lock held energised burns out (ticket 35).
#>

function New-W1ModbusSession {
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port,
        [int]$UnitId = 255,
        [int]$TimeoutMs = 1000
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync($HostName, $Port).Wait($TimeoutMs)) {
            throw "Modbus TCP connect to ${HostName}:$Port timed out after $TimeoutMs ms."
        }
    }
    catch {
        $client.Dispose()
        throw
    }
    $client.NoDelay = $true
    $stream = $client.GetStream()
    $stream.ReadTimeout = $TimeoutMs
    $stream.WriteTimeout = $TimeoutMs
    [pscustomobject]@{
        Client        = $client
        Stream        = $stream
        UnitId        = [byte]$UnitId
        TransactionId = 0
    }
}

function Close-W1ModbusSession {
    param($Session)

    if ($Session) {
        $Session.Stream.Dispose()
        $Session.Client.Dispose()
    }
}

function Read-W1Exactly {
    param([Parameter(Mandatory)]$Stream, [Parameter(Mandatory)][int]$Count)

    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw 'Modbus TCP connection closed in the middle of a response.' }
        $offset += $read
    }
    , $buffer
}

function Invoke-W1ModbusPdu {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][byte[]]$Pdu)

    $Session.TransactionId = ($Session.TransactionId + 1) % 65536
    $transactionId = $Session.TransactionId
    $length = $Pdu.Length + 1
    [byte[]]$request = @(
        (($transactionId -shr 8) -band 0xFF), ($transactionId -band 0xFF),
        0, 0,
        (($length -shr 8) -band 0xFF), ($length -band 0xFF),
        $Session.UnitId) + $Pdu
    $Session.Stream.Write($request, 0, $request.Length)

    $header = Read-W1Exactly -Stream $Session.Stream -Count 7
    $responseTransactionId = ($header[0] -shl 8) -bor $header[1]
    $protocolId = ($header[2] -shl 8) -bor $header[3]
    $responseLength = ($header[4] -shl 8) -bor $header[5]
    if ($responseTransactionId -ne $transactionId -or $protocolId -ne 0 -or $header[6] -ne $Session.UnitId -or
        $responseLength -lt 2 -or $responseLength -gt 254) {
        throw "Invalid Modbus TCP response header (transaction $responseTransactionId, protocol $protocolId, unit $($header[6]), length $responseLength)."
    }

    $response = Read-W1Exactly -Stream $Session.Stream -Count ($responseLength - 1)
    if ($response[0] -eq ($Pdu[0] -bor 0x80)) {
        $code = $response.Length -gt 1 ? $response[1] : 0
        throw ('Modbus exception: function 0x{0:X2}, exception code 0x{1:X2}.' -f $Pdu[0], $code)
    }
    if ($response[0] -ne $Pdu[0]) {
        throw ('Modbus function mismatch: sent 0x{0:X2}, received 0x{1:X2}.' -f $Pdu[0], $response[0])
    }
    , $response
}

function Read-W1Bits {
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][byte]$Function,
        [Parameter(Mandatory)][int]$Start,
        [Parameter(Mandatory)][int]$Count
    )

    [byte[]]$pdu = @($Function, (($Start -shr 8) -band 0xFF), ($Start -band 0xFF), (($Count -shr 8) -band 0xFF), ($Count -band 0xFF))
    $response = Invoke-W1ModbusPdu -Session $Session -Pdu $pdu
    $byteCount = [int][math]::Floor(($Count + 7) / 8)
    if ($response.Length -lt 2 -or $response[1] -ne $byteCount -or $response.Length -ne $response[1] + 2) {
        throw ('FC{0:X2} response byte count is invalid.' -f $Function)
    }

    $bits = [int[]]::new($Count)
    for ($index = 0; $index -lt $Count; $index++) {
        $byte = $response[2 + [int][math]::Floor($index / 8)]
        $bits[$index] = ($byte -band (1 -shl ($index % 8))) -ne 0 ? 1 : 0
    }
    , $bits
}

function Get-W1IoImage {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)]$Endpoint, $Clock)

    $outputs = Read-W1Bits -Session $Session -Function 1 -Start $Endpoint.DoStart -Count $Endpoint.Channels
    $inputs = Read-W1Bits -Session $Session -Function 2 -Start $Endpoint.DiStart -Count $Endpoint.Channels
    [ordered]@{
        at        = (Get-Date).ToString('o')
        elapsedMs = $Clock ? [int]$Clock.ElapsedMilliseconds : 0
        do        = $outputs
        di        = $inputs
    }
}

function Set-W1Coil {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][int]$Address, [Parameter(Mandatory)][bool]$On)

    $value = $On ? 0xFF00 : 0x0000
    [byte[]]$pdu = @(5, (($Address -shr 8) -band 0xFF), ($Address -band 0xFF), (($value -shr 8) -band 0xFF), ($value -band 0xFF))
    $response = Invoke-W1ModbusPdu -Session $Session -Pdu $pdu
    if (($response -join ',') -ne ($pdu -join ',')) {
        throw 'FC05 response does not echo the request; the output state is unknown.'
    }
}

<#
One exchange with the module, start to finish on one connection.

  image  read the output and input image once
  pulse  read the image, fire FC05 0xFF00 on one unlock output, then follow the image like watch; if the
         output is still set at ResetDeadlineMs, clear it at once and say so (forcedReset)
  watch  read the image repeatedly for DurationMs

Following stops early once UntilDiChannel has read UntilValue for StableMs. With ArmValue set, that
condition only counts after the channel has first read ArmValue -- "released, then locked again" --
and if it has not read ArmValue by ArmTimeoutMs the operation stops there (armTimedOut). ChangesOnly
keeps only the images that differ from the one before, so a long follow stays small.

Endpoint is a hashtable: HostName, Port, UnitId, DoStart, DiStart, Channels.
#>
function Invoke-W1IoOperation {
    param(
        [Parameter(Mandatory)][hashtable]$Endpoint,
        [Parameter(Mandatory)][ValidateSet('image', 'pulse', 'watch')][string]$Operation,
        [int]$Channel = -1,
        [int]$DurationMs = 3000,
        [int]$IntervalMs = 20,
        [int]$UntilDiChannel = -1,
        [int]$UntilValue = -1,
        [int]$StableMs = 300,
        [int]$ArmValue = -1,
        [int]$ArmTimeoutMs = 0,
        [int]$ResetDeadlineMs = 3000,
        [switch]$ChangesOnly
    )

    $session = New-W1ModbusSession -HostName $Endpoint.HostName -Port $Endpoint.Port -UnitId $Endpoint.UnitId
    try {
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $result = [ordered]@{
            operation = $Operation
            channel   = $Channel
            before    = Get-W1IoImage -Session $session -Endpoint $Endpoint -Clock $clock
        }
        if ($Operation -eq 'image') { return $result }

        if ($Operation -eq 'pulse') {
            if ($Channel -lt 0 -or $Channel -ge $Endpoint.Channels) {
                throw "pulse needs a channel in 0..$($Endpoint.Channels - 1)."
            }
            $clock.Restart()
            Set-W1Coil -Session $session -Address ($Endpoint.DoStart + $Channel) -On $true
            $result.pulseSentAtMs = [int]$clock.ElapsedMilliseconds
            $result.forcedReset = $false
        }

        $trace = [System.Collections.Generic.List[object]]::new()
        $stableSince = $null
        $armed = $ArmValue -lt 0
        $previous = $null
        $result.untilMet = $false
        $result.armTimedOut = $false
        while ($clock.ElapsedMilliseconds -lt $DurationMs) {
            $image = Get-W1IoImage -Session $session -Endpoint $Endpoint -Clock $clock
            $changed = $null -eq $previous -or ($image.do -join '') -ne ($previous.do -join '') -or ($image.di -join '') -ne ($previous.di -join '')
            if (-not $ChangesOnly -or $changed) { $trace.Add($image) }
            $previous = $image

            if ($Operation -eq 'pulse' -and -not $result.forcedReset -and $image.elapsedMs -ge $ResetDeadlineMs -and $image.do[$Channel] -eq 1) {
                Set-W1Coil -Session $session -Address ($Endpoint.DoStart + $Channel) -On $false
                $result.forcedReset = $true
                $result.forcedResetAtMs = $image.elapsedMs
            }

            if ($UntilDiChannel -ge 0) {
                if (-not $armed) {
                    if ($image.di[$UntilDiChannel] -eq $ArmValue) {
                        $armed = $true
                    }
                    elseif ($ArmTimeoutMs -gt 0 -and $image.elapsedMs -ge $ArmTimeoutMs) {
                        $result.armTimedOut = $true
                        break
                    }
                }
                if ($armed) {
                    if ($image.di[$UntilDiChannel] -eq $UntilValue) {
                        $stableSince ??= $image.elapsedMs
                        if ($image.elapsedMs - $stableSince -ge $StableMs) {
                            $result.untilMet = $true
                            break
                        }
                    }
                    else {
                        $stableSince = $null
                    }
                }
            }
            Start-Sleep -Milliseconds $IntervalMs
        }
        $result.trace = $trace

        if ($Operation -eq 'pulse' -and -not $result.forcedReset) {
            $last = Get-W1IoImage -Session $session -Endpoint $Endpoint -Clock $clock
            if ($last.do[$Channel] -eq 1) {
                Set-W1Coil -Session $session -Address ($Endpoint.DoStart + $Channel) -On $false
                $result.forcedReset = $true
                $result.forcedResetAtMs = $last.elapsedMs
            }
        }

        $result.after = Get-W1IoImage -Session $session -Endpoint $Endpoint -Clock $clock
        $result
    }
    finally {
        Close-W1ModbusSession -Session $session
    }
}
