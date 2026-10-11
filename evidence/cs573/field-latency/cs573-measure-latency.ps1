#Requires -Version 7
# control-server#573: latency of the v2 instance's onboard vehicle-safety requests and of the RIoT reads inside them.
# Read-only, streamed line by line, BelowNormal priority. Reads only the v2 instance's own log directory.
param([string[]]$Files = @(
    'C:\ProgramData\8005\ControlServer.V2\logs\controlserver-20261010.ndjson',
    'C:\ProgramData\8005\ControlServer.V2\logs\controlserver-20261011.ndjson'))
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$series = @{}
foreach ($k in 'safety', 'pageInSafety', 'pageElsewhere', 'vehicleInfoInSafety') { $series[$k] = [Collections.Generic.List[double]]::new() }
$status = @{}
$first = $null; $last = $null
$elapsedRx = [regex]'"Elapsed":([0-9.Ee+-]+)'
$clientRx = [regex]'"ElapsedMilliseconds":([0-9.Ee+-]+)'
$statusRx = [regex]'"StatusCode":(\d+)'
foreach ($file in $Files) {
    $reader = [IO.StreamReader]::new($file)
    $fileFirst = $null; $fileLast = $null
    while ($null -ne ($line = $reader.ReadLine())) {
        if ($line.Length -lt 40) { continue }
        $t = $line.Substring(7, 28)
        $fileFirst ??= $t; $fileLast = $t
        $inSafety = $line.Contains('"RequestPath":"/api/onboard/v1/vehicle-safety"')
        if ($line.Contains('responded {StatusCode} in {Elapsed') -and $inSafety) {
            $series.safety.Add([double]$elapsedRx.Match($line).Groups[1].Value)
            $s = $statusRx.Match($line).Groups[1].Value; $status[$s] = 1 + ($status[$s] ?? 0)
            continue
        }
        if ($line.Contains('"Name":"RequestEnd"') -and $line.Contains('ClientHandler')) {
            $ms = [double]$clientRx.Match($line).Groups[1].Value
            if ($line.Contains('orderRecord?pageNum=')) { if ($inSafety) { $series.pageInSafety.Add($ms) } else { $series.pageElsewhere.Add($ms) } }
            elseif ($inSafety -and $line.Contains('getVehicleInfo')) { $series.vehicleInfoInSafety.Add($ms) }
        }
    }
    $reader.Dispose()
    "FILE $([IO.Path]::GetFileName($file)) first=$fileFirst last=$fileLast"
}
function Q($list, [double]$q) { $a = $list.ToArray(); [Array]::Sort($a); if ($a.Count -eq 0) { return 'n/a' }; '{0:0.0}' -f $a[[Math]::Min($a.Count - 1, [int][Math]::Ceiling($q * $a.Count) - 1)] }
foreach ($k in 'safety', 'pageInSafety', 'vehicleInfoInSafety', 'pageElsewhere') {
    $l = $series[$k]
    $over1 = @($l | Where-Object { $_ -gt 1000 }).Count; $over2 = @($l | Where-Object { $_ -gt 2000 }).Count; $over3 = @($l | Where-Object { $_ -gt 3000 }).Count
    "$k n=$($l.Count) p50=$(Q $l 0.5) p95=$(Q $l 0.95) p99=$(Q $l 0.99) p999=$(Q $l 0.999) max=$(Q $l 1.0) >1s=$over1 >2s=$over2 >3s=$over3"
}
"safety status: " + (($status.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
