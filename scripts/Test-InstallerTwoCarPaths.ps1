#Requires -Version 7
<#
.SYNOPSIS
Self-check for control-server#649: a two-car v2 parallel instance goes through both product scripts -- the upgrade's
read-only safety projection check with each vehicle's own credential, and a first install whose first readiness read is
already ready.
.DESCRIPTION
Runs offline and unelevated against a scratch directory under %TEMP%; it touches no service, no machine variable and no
real data root. The one thing it writes outside %TEMP% is a throwaway key under HKCU\Software\cs649-selftest, removed at
the end (see below).

Each case runs a scratch copy of the real script in a child pwsh under Set-StrictMode -Version Latest (the parallel
installer sets StrictMode and both scripts inherit it there; control-server#605). The copies differ from the source in
counted ways only: the one top-level Assert-Administrator call is removed, [Environment]:: is redirected to a stub class
(the machine-scope shared credential), and -- Install only -- the new service's registry key is a throwaway HKCU key
whose ACL is left alone (the ACL would lock the unelevated test out of deleting it). The service control manager,
New-Service, sc.exe, Set-Acl, Get-CimInstance and the service's registry Environment (Get-ItemProperty) are replaced by
functions. Every HTTP call goes to a loopback double of the Host: /health/live, /version, /health/ready, and
/api/onboard/v1/vehicle-safety, which decides the vehicle by the bearer exactly as OnboardVehicleSafetyEndpoints does --
with per-vehicle credentials the shared one is answered 401 and a vehicleKey that is not the credential's 403. The double
logs which credential (by vehicle, never by value) each request carried.

The credentials are fake; every result, diagnostic and error is still checked not to contain any of them.

-UpdateScript and -InstallScript point the cases at another copy (the pre-fix scripts, or a mutation).

Exit code 0 when every check passes, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$UpdateScript,
    [string]$InstallScript
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$updatePath = $UpdateScript ? (Resolve-Path -LiteralPath $UpdateScript).Path : (Join-Path $repoRoot 'scripts/Update-ControlServerLocal.ps1')
$installPath = $InstallScript ? (Resolve-Path -LiteralPath $InstallScript).Path : (Join-Path $repoRoot 'scripts/Install-ControlServerLocal.ps1')
$listenerModule = Join-Path $PSScriptRoot 'LoopbackListener.psm1'
$id = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$scratch = Join-Path ([IO.Path]::GetTempPath()) "cs649-selftest-$id"
$registryKey = "Software\cs649-selftest\$id"
New-Item -ItemType Directory -Path $scratch | Out-Null
$failures = [Collections.Generic.List[string]]::new()

$shared = "cs649-shared-$id"
$agv02 = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4'
$agv03 = 'BROKERX-7daca4ee91da498d8026c68b7b941127'
$agv02Variable = 'CONTROL_SERVER_V2_ONBOARD_SAFETY_CREDENTIAL_RIOT59'
$agv03Variable = 'CONTROL_SERVER_V2_ONBOARD_SAFETY_CREDENTIAL_RIOT60'
$agv02Value = "cs649-agv02-$id"
$agv03Value = "cs649-agv03-$id"
$secrets = @($shared, $agv02Value, $agv03Value, 'cs649-unknown')

function Write-Result([bool]$Ok, [string]$Name, [string]$Detail = '') {
    Write-Host ("[{0}] {1}{2}" -f ($Ok ? 'PASS' : 'FAIL'), $Name, ($Detail ? " -- $Detail" : ''))
    if (-not $Ok) { $failures.Add($Name) }
}

function Read-Text([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return [IO.File]::ReadAllText($Path) }
    return ''
}

function Get-Short([string]$Text) { return $Text.Substring(0, [Math]::Min(220, $Text.Length)) }

try {
    # --- Scratch copies -------------------------------------------------------------------------------------------------
    function New-ScriptCopy([string]$Source, [string]$Destination, [hashtable]$Replacements) {
        $text = [IO.File]::ReadAllText($Source)
        $call = '(?m)^Assert-Administrator(?=\r?$)'
        $calls = [regex]::Matches($text, $call).Count
        Write-Result ($calls -eq 1) "$(Split-Path -Leaf $Source): exactly one top-level Assert-Administrator call to remove" "found=$calls"
        $text = [regex]::Replace($text, $call, '# Assert-Administrator removed by Test-InstallerTwoCarPaths.ps1')
        foreach ($key in $Replacements.Keys) {
            $count = ([regex]::Matches($text, [regex]::Escape($key))).Count
            Write-Result ($count -eq 1) "$(Split-Path -Leaf $Source): one '$key' to replace" "found=$count"
            $text = $text.Replace($key, $Replacements[$key])
        }
        $environmentCount = ([regex]::Matches($text, [regex]::Escape('[Environment]::'))).Count
        Write-Result ($environmentCount -ge 3) "$(Split-Path -Leaf $Source): [Environment]:: redirected to the stub" "occurrences=$environmentCount"
        $text = $text.Replace('[Environment]::', '[Cs649SelftestEnvironment]::')
        [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
    }
    $updateCopy = Join-Path $scratch 'Update-ControlServerLocal.selftest.ps1'
    $installCopy = Join-Path $scratch 'Install-ControlServerLocal.selftest.ps1'
    New-ScriptCopy $updatePath $updateCopy @{}
    New-ScriptCopy $installPath $installCopy @{
        '[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistrySubKey, $true)' =
            '[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($env:CS649_SELFTEST_REGISTRY_KEY, $true)'
        'Set-RestrictedRegistryAcl $serviceRegistryKey' = '# Set-RestrictedRegistryAcl removed by Test-InstallerTwoCarPaths.ps1'
    }

    # --- The runner: stubs, the Host double, one product script -----------------------------------------------------------
    $runner = Join-Path $scratch 'runner.ps1'
    [IO.File]::WriteAllText($runner, @'
#Requires -Version 7
param([string]$Kind, [string]$Script, [string]$Case, [string]$ListenerModule, [int]$Port)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$caseConfig = Get-Content -Raw -LiteralPath "$Case/case.json" | ConvertFrom-Json -AsHashtable
Add-Type -TypeDefinition @"
public static class Cs649SelftestEnvironment {
    public static string NewLine { get { return System.Environment.NewLine; } }
    public static string GetEnvironmentVariable(string name, System.EnvironmentVariableTarget target) {
        if (name == "CONTROL_SERVER_RIOT_CALL_API_KEY" && target == System.EnvironmentVariableTarget.User) return "stub-riot";
        if (name == "CONTROL_SERVER_ONBOARD_CREDENTIAL" && target == System.EnvironmentVariableTarget.Machine)
            return System.Environment.GetEnvironmentVariable("CS649_SELFTEST_SHARED");
        return null;
    }
    public static void SetEnvironmentVariable(string name, string value, System.EnvironmentVariableTarget target) { }
}
"@

# The Host double. Vehicle by bearer, as OnboardVehicleSafetyEndpoints.ResolveVehicle; the log names vehicles, not values.
# -Port: the one the Update case wrote into the retained Health:url; otherwise any free one.
Import-Module $ListenerModule -Force
$started = if ($Port -gt 0) {
    $fixed = [System.Net.HttpListener]::new(); $fixed.Prefixes.Add("http://localhost:$Port/"); $fixed.Start()
    [pscustomobject]@{ Listener = $fixed; Port = $Port }
} else { Start-LoopbackListener }
$listener = $started.Listener
Set-Content -LiteralPath "$Case/port.txt" -Value $started.Port
$requestLog = [Collections.ArrayList]::Synchronized([Collections.ArrayList]::new())
$double = [powershell]::Create()
$null = $double.AddScript({
    param($Listener, $Config, $Log)
    function Send($Context, [int]$Status, [string]$Body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
        $Context.Response.StatusCode = $Status
        $Context.Response.ContentType = 'application/json'
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $Context.Response.Close()
    }
    while ($Listener.IsListening) {
        try { $context = $Listener.GetContext() } catch { break }
        $path = $context.Request.Url.AbsolutePath
        $authorization = [string]$context.Request.Headers['Authorization']
        $bearer = $authorization.StartsWith('Bearer ') ? $authorization.Substring(7) : $null
        $claimed = $context.Request.QueryString['vehicleKey']
        $perVehicle = $Config['perVehicle']
        $vehicle = if ($null -eq $bearer) { $null }
            elseif ($perVehicle.Count -gt 0) { $perVehicle.ContainsKey($bearer) ? $perVehicle[$bearer] : $null }
            elseif ($bearer -ceq $Config['shared']) { $Config['singleVehicle'] } else { $null }
        $who = if ($null -ne $bearer -and $bearer -ceq $Config['shared']) { 'shared' } elseif ($null -ne $vehicle) { $vehicle } else { 'unknown' }
        if ($path -eq '/api/onboard/v1/vehicle-safety') { [void]$Log.Add("vehicle-safety|credential=$who|claimed=$claimed") }
        switch ($path) {
            '/health/live' { Send $context 200 '{"status":"live"}' }
            '/version' { Send $context 200 '{"protocolTag":"selftest","protocolCommit":"selftest"}' }
            '/health/ready' { if ($Config['ready']) { Send $context 200 '{"status":"ready"}' }
                else { Send $context 503 ('{"status":"not-ready","reason":"' + $Config['notReadyReason'] + '"}') } }
            '/api/onboard/v1/vehicle-safety' {
                if ($null -eq $vehicle) { Send $context 401 '' }
                elseif ($null -ne $claimed -and $claimed -cne $vehicle) {
                    Send $context 403 '{"title":"Vehicle safety projection refused","detail":"VEHICLE_KEY_NOT_THE_CREDENTIALS"}'
                }
                else { Send $context 200 ('{"vehicleKey":"' + $vehicle + '","motionState":"STOPPED","observedAt":"2026-10-12T00:00:00Z","source":"selftest","reasonCodes":["A","B"]}') }
            }
            default { Send $context 404 '' }
        }
    }
}).AddArgument($listener).AddArgument($caseConfig['double']).AddArgument($requestLog)
$doubleHandle = $double.BeginInvoke()

$global:ServiceStatus = $Kind -eq 'Update' ? 'Running' : $null
$global:DataRootForStub = "$Case/dataroot"
function Get-Service {
    param([string]$Name, $ErrorAction)
    if ($null -eq $global:ServiceStatus) { return $null }
    [pscustomobject]@{ Name = $Name; Status = $global:ServiceStatus }
}
function Stop-Service { param([string]$Name, [switch]$Force, $ErrorAction) if ($global:ServiceStatus) { $global:ServiceStatus = 'Stopped' } }
# Stands in for the Host's first start: the database its migrations create and the log file it writes.
function Start-Service {
    param([string]$Name)
    $global:ServiceStatus = 'Running'
    $data = New-Item -ItemType Directory -Path "$global:DataRootForStub/data" -Force
    $logs = New-Item -ItemType Directory -Path "$global:DataRootForStub/logs" -Force
    [IO.File]::WriteAllText((Join-Path $data 'controlserver.db'), 'db')
    [IO.File]::WriteAllText((Join-Path $logs 'controlserver-20261012.ndjson'), '{}')
}
function Restart-Service { param([string]$Name, [switch]$Force) $global:ServiceStatus = 'Running' }
function New-Service { param($Name, $BinaryPathName, $DisplayName, $Description, $StartupType) $global:ServiceStatus = 'Stopped' }
function sc.exe { }
function Set-Acl { param([string]$LiteralPath, $AclObject) }
function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{ StartMode = 'Auto' } }
# The service's registry Environment, as Update-ControlServerLocal.ps1 reads it.
function Get-ItemProperty {
    param([string]$LiteralPath, [string]$Name, $ErrorAction)
    $environment = $caseConfig['serviceEnvironment']
    if ($null -eq $environment) { return $null }
    [pscustomobject]@{ Environment = [string[]]@($environment) }
}

$common = @{ PackagePath = "$Case/package"; ResultPath = "$Case/result.json"; DiagnosticPath = "$Case/diagnostic.log"
    ServiceName = 'cs649-selftest'; InstallRoot = "$Case/install"; DataRoot = "$Case/dataroot"; BackupRoot = "$Case/backups" }
try {
    if ($Kind -eq 'Update') {
        & $Script @common -CertificatePasswordVariable 'CS649_SELFTEST_NO_SUCH_VARIABLE' -VerifySafetyProjectionReadOnly
    }
    else {
        & $Script @common -SkipMachineEnvironmentInjection -ListenAddress 'localhost' -HealthBindAddress 'localhost' -HealthPort $started.Port
    }
    Set-Content -LiteralPath "$Case/outcome.txt" -Value 'COMPLETED'
}
catch {
    $inner = $_.Exception -is [AggregateException] ? (($_.Exception.InnerExceptions | ForEach-Object Message) -join ' || ') : ''
    Set-Content -LiteralPath "$Case/outcome.txt" -Value ('THREW: ' + $_.Exception.Message + ' || ' + $inner)
}
finally {
    $listener.Stop()
    $listener.Close()
    try { $null = $double.EndInvoke($doubleHandle) } catch { }
    $double.Dispose()
    Set-Content -LiteralPath "$Case/requests.txt" -Value (@($requestLog) -join "`n")
}
'@, [Text.UTF8Encoding]::new($false))

    function New-Case([string]$Name, [string]$Kind, [hashtable]$Double, [object]$VehicleCredentials, [string[]]$ServiceEnvironment) {
        $case = New-Item -ItemType Directory -Path (Join-Path $scratch $Name)
        $package = New-Item -ItemType Directory -Path (Join-Path $case 'package')
        [IO.File]::WriteAllText((Join-Path $package 'ControlServer.Host.exe'), 'new binary')
        $hash = (Get-FileHash -LiteralPath (Join-Path $package 'ControlServer.Host.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest = [ordered]@{ schemaVersion = 1; product = '8005 AGV ControlServer'; sourceCommit = 'selftest'
            files = @([ordered]@{ path = 'ControlServer.Host.exe'; sha256 = $hash }) }
        [IO.File]::WriteAllText((Join-Path $package 'deployment-manifest.json'), ($manifest | ConvertTo-Json -Depth 4))
        [IO.File]::WriteAllText((Join-Path $case 'case.json'),
            ([ordered]@{ double = $Double; serviceEnvironment = $ServiceEnvironment } | ConvertTo-Json -Depth 6))
        if ($Kind -eq 'Update') {
            New-Item -ItemType Directory -Path (Join-Path $case 'dataroot/data') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $case 'install') | Out-Null
            [IO.File]::WriteAllText((Join-Path $case 'install/ControlServer.Host.exe'), 'old binary')
        }
        return [pscustomobject]@{ Root = $case.FullName; Kind = $Kind; VehicleCredentials = $VehicleCredentials }
    }

    function Invoke-Case($Case) {
        $script = $Case.Kind -eq 'Update' ? $updateCopy : $installCopy
        $env:CS649_SELFTEST_SHARED = $shared
        $env:CS649_SELFTEST_REGISTRY_KEY = "$registryKey\$(Split-Path -Leaf $Case.Root)"
        if ($Case.Kind -eq 'Update') {
            # The Update reads Health:url from the retained configuration; a port is reserved here and handed to the runner.
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
            $configuration = [ordered]@{
                Health = [ordered]@{ url = "http://localhost:$port" }
                JourneyRuntime = [ordered]@{ enabled = $false }
                OnboardSafetyProjection = [ordered]@{ enabled = $true; credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL' }
            }
            if ($null -ne $Case.VehicleCredentials) { $configuration.OnboardSafetyProjection['vehicleCredentials'] = @($Case.VehicleCredentials) }
            [IO.File]::WriteAllText((Join-Path $Case.Root 'install/appsettings.Production.json'), ($configuration | ConvertTo-Json -Depth 6))
        }
        else { $port = 0 }
        & pwsh -NoProfile -NonInteractive -File $runner -Kind $Case.Kind -Script $script -Case $Case.Root -ListenerModule $listenerModule -Port $port
        $resultPath = Join-Path $Case.Root 'result.json'
        return [pscustomobject]@{
            Outcome = (Read-Text (Join-Path $Case.Root 'outcome.txt')).Trim()
            Diagnostic = Read-Text (Join-Path $Case.Root 'diagnostic.log')
            ResultText = Read-Text $resultPath
            Result = (Test-Path -LiteralPath $resultPath) ? (Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json) : $null
            Requests = @((Read-Text (Join-Path $Case.Root 'requests.txt')).Trim() -split "`r?`n" | Where-Object { $_ })
            InstalledBinary = Read-Text (Join-Path $Case.Root 'install/ControlServer.Host.exe')
        }
    }

    function Test-NoSecret($Run, [string]$Name) {
        $text = $Run.Outcome + $Run.Diagnostic + $Run.ResultText
        $leaked = @($secrets | Where-Object { $text.Contains($_) })
        Write-Result ($leaked.Count -eq 0) "$Name`: no credential value in the outcome, the diagnostic or the result" "leaked=$($leaked.Count)"
    }

    $twoCarRows = @([ordered]@{ vehicleKey = $agv02; credentialEnvironmentVariable = $agv02Variable },
        [ordered]@{ vehicleKey = $agv03; credentialEnvironmentVariable = $agv03Variable })
    $twoCarDouble = @{ shared = $shared; perVehicle = @{ $agv02Value = $agv02; $agv03Value = $agv03 }; ready = $true }
    $baseEnvironment = @('DOTNET_ENVIRONMENT=Production', 'CONTROL_SERVER_RIOT_CALL_API_KEY=stub-riot', "CONTROL_SERVER_ONBOARD_CREDENTIAL=$shared")

    # --- Update -------------------------------------------------------------------------------------------------------
    # U1: the two-car instance REQ-0178 failed on (2026-10-12), each vehicle's credential in the service Environment.
    $run = Invoke-Case (New-Case 'u1-two-car' 'Update' $twoCarDouble $twoCarRows ($baseEnvironment + "$agv02Variable=$agv02Value" + "$agv03Variable=$agv03Value"))
    $rows = @($run.Result?.safetyProjection?.vehicles)
    Write-Result ($run.Outcome -eq 'COMPLETED' -and $run.Result?.result -eq 'PASS') 'U1 two cars: the upgrade completes with PASS' (Get-Short $run.Outcome)
    Write-Result ($rows.Count -eq 2 -and $rows[0].vehicleKey -ceq $agv02 -and $rows[0].credentialEnvironmentVariable -ceq $agv02Variable -and
        $rows[1].vehicleKey -ceq $agv03 -and $rows[1].credentialEnvironmentVariable -ceq $agv03Variable -and
        @($rows | Where-Object { $_.motionState -eq 'STOPPED' -and $_.reasonCount -eq 2 }).Count -eq 2) `
        'U1 two cars: the result has one projection row per vehicle, naming its variable' "rows=$($rows.Count)"
    $expected = @("vehicle-safety|credential=$agv02|claimed=$agv02", "vehicle-safety|credential=$agv03|claimed=$agv03")
    Write-Result ((@($run.Requests) -join ',') -ceq (($expected + $expected) -join ',')) `
        'U1 two cars: each vehicle is read with its own credential and names itself, before and after the restart; the shared credential is never sent' `
        ($run.Requests -join ',')
    Test-NoSecret $run 'U1'

    # U2: what the 2026-10-12 upgrade did -- the shared credential against a two-car Host. Its configuration lists no
    # per-vehicle credentials here, so the fixed script sends the shared one too, and the refusal must say why.
    $run = Invoke-Case (New-Case 'u2-shared-rejected' 'Update' $twoCarDouble $null $baseEnvironment)
    Write-Result ($run.Outcome -like 'THREW: SAFETY_PROJECTION_CREDENTIAL_REJECTED:*' -and $run.Outcome.Contains('HTTP 401') -and
        $run.Outcome.Contains('CONTROL_SERVER_ONBOARD_CREDENTIAL') -and $run.Outcome.Contains('vehicleCredentials')) `
        'U2 shared credential refused: SAFETY_PROJECTION_CREDENTIAL_REJECTED names the shared variable and why' (Get-Short $run.Outcome)
    Write-Result ($run.Diagnostic -match 'rollback-complete' -and $run.InstalledBinary -eq 'old binary') 'U2 shared credential refused: rolled back to the old binary'
    Test-NoSecret $run 'U2'

    # U3: one vehicle's variable holds a value the Host does not know.
    $run = Invoke-Case (New-Case 'u3-vehicle-rejected' 'Update' $twoCarDouble $twoCarRows ($baseEnvironment + "$agv02Variable=$agv02Value" + "$agv03Variable=cs649-unknown"))
    Write-Result ($run.Outcome -like 'THREW: SAFETY_PROJECTION_CREDENTIAL_REJECTED:*' -and $run.Outcome.Contains($agv03) -and
        $run.Outcome.Contains($agv03Variable) -and -not $run.Outcome.Contains($agv02Variable)) `
        'U3 one vehicle''s credential refused: SAFETY_PROJECTION_CREDENTIAL_REJECTED names that vehicle and its variable' (Get-Short $run.Outcome)
    Write-Result ($run.Diagnostic -match 'rollback-complete' -and $run.InstalledBinary -eq 'old binary') 'U3 one vehicle''s credential refused: rolled back'
    Test-NoSecret $run 'U3'

    # U4: the two variables crossed: each value is the other vehicle's. The Host answers the claimed key 403.
    $run = Invoke-Case (New-Case 'u4-crossed' 'Update' $twoCarDouble $twoCarRows ($baseEnvironment + "$agv02Variable=$agv03Value" + "$agv03Variable=$agv02Value"))
    Write-Result ($run.Outcome -like 'THREW: SAFETY_PROJECTION_VEHICLE_MISMATCH:*' -and $run.Outcome.Contains('VEHICLE_KEY_NOT_THE_CREDENTIALS') -and
        $run.Outcome.Contains($agv02)) 'U4 crossed credentials: SAFETY_PROJECTION_VEHICLE_MISMATCH' (Get-Short $run.Outcome)
    Write-Result ($run.Diagnostic -match 'rollback-complete' -and $run.InstalledBinary -eq 'old binary') 'U4 crossed credentials: rolled back'
    Test-NoSecret $run 'U4'

    # U5: a listed vehicle has no value in the service Environment: refused in the preflight, nothing touched.
    $run = Invoke-Case (New-Case 'u5-missing' 'Update' $twoCarDouble $twoCarRows ($baseEnvironment + "$agv02Variable=$agv02Value"))
    Write-Result ($run.Outcome -like 'THREW: SAFETY_PROJECTION_VEHICLE_CREDENTIAL_MISSING:*' -and $run.Outcome.Contains($agv03) -and
        $run.Outcome.Contains($agv03Variable)) 'U5 missing per-vehicle credential: SAFETY_PROJECTION_VEHICLE_CREDENTIAL_MISSING names the vehicle and variable' (Get-Short $run.Outcome)
    Write-Result ($run.Diagnostic -notmatch 'service-stopped' -and $run.Diagnostic -notmatch 'preflight-complete' -and $run.InstalledBinary -eq 'old binary' -and
        $run.Requests.Count -eq 0) 'U5 missing per-vehicle credential: refused before the service was stopped; no request sent' "requests=$($run.Requests.Count)"
    Test-NoSecret $run 'U5'

    # U6: a single-car (or factory) installation: unchanged -- the shared credential, no vehicleKey, the old result shape.
    $singleDouble = @{ shared = $shared; perVehicle = @{}; singleVehicle = $agv02; ready = $true }
    $run = Invoke-Case (New-Case 'u6-single-car' 'Update' $singleDouble $null $baseEnvironment)
    Write-Result ($run.Outcome -eq 'COMPLETED' -and $run.Result?.result -eq 'PASS') 'U6 single car: the upgrade completes with PASS' (Get-Short $run.Outcome)
    Write-Result ((@($run.Requests) -join ',') -ceq 'vehicle-safety|credential=shared|claimed=,vehicle-safety|credential=shared|claimed=') `
        'U6 single car: two reads with the shared credential and no vehicleKey, as before' ($run.Requests -join ',')
    $projection = $run.Result?.safetyProjection
    Write-Result ($null -ne $projection -and (@($projection.PSObject.Properties.Name) -join ',') -ceq 'httpStatus,motionState,source,reasonCount' -and
        $projection.motionState -eq 'STOPPED' -and $projection.reasonCount -eq 2) 'U6 single car: the result''s safetyProjection keeps its shape' `
        ($null -eq $projection ? '<none>' : (@($projection.PSObject.Properties.Name) -join ','))
    Test-NoSecret $run 'U6'

    # --- Install ------------------------------------------------------------------------------------------------------
    # I1: the 2026-10-12 first install: the reused data root holds a ready session, so the first read is ready (no reason).
    $run = Invoke-Case (New-Case 'i1-ready-first' 'Install' @{ shared = $shared; perVehicle = @{}; ready = $true } $null $null)
    Write-Result ($run.Outcome -eq 'COMPLETED' -and $run.Result?.result -eq 'PASS') 'I1 first read ready: the install completes with PASS' (Get-Short $run.Outcome)
    Write-Result ($run.Result?.firstStartReadiness?.status -eq 'ready' -and $null -eq $run.Result?.firstStartReadiness?.reason -and
        $run.ResultText -match '"reason":\s*null') 'I1 first read ready: firstStartReadiness is ready with reason null'
    Test-NoSecret $run 'I1'

    # I2: a fresh data root: the first read is not-ready with a reason, recorded as it always was.
    $run = Invoke-Case (New-Case 'i2-not-ready' 'Install' @{ shared = $shared; perVehicle = @{}; ready = $false; notReadyReason = 'RECOVERY_HANDSHAKE_REQUIRED' } $null $null)
    Write-Result ($run.Outcome -eq 'COMPLETED' -and $run.Result?.result -eq 'PASS') 'I2 first read not-ready: the install completes with PASS' (Get-Short $run.Outcome)
    Write-Result ($run.Result?.firstStartReadiness?.status -eq 'not-ready' -and $run.Result?.firstStartReadiness?.reason -eq 'RECOVERY_HANDSHAKE_REQUIRED') `
        'I2 first read not-ready: firstStartReadiness keeps the reason'
    Test-NoSecret $run 'I2'

    # I3: not-ready because the database is unavailable still stops the install.
    $run = Invoke-Case (New-Case 'i3-database-unavailable' 'Install' @{ shared = $shared; perVehicle = @{}; ready = $false; notReadyReason = 'DATABASE_UNAVAILABLE' } $null $null)
    Write-Result ($run.Outcome -like 'THREW: Readiness reports the database is unavailable after migration.*' -and $null -eq $run.Result) `
        'I3 database unavailable: the install refuses as before' (Get-Short $run.Outcome)
}
catch {
    Write-Result $false "the self-test itself stopped: $($_.Exception.Message)" "line $($_.InvocationInfo.ScriptLineNumber)"
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "HKCU:\$registryKey" -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem -LiteralPath 'HKCU:\Software\cs649-selftest' -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath 'HKCU:\Software\cs649-selftest' -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s): $($failures -join '; ')"
    exit 1
}
Write-Host 'All checks passed.'
exit 0
