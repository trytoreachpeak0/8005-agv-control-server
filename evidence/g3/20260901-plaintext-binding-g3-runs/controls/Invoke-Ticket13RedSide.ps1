#requires -Version 7
<#
Ticket 13 red-side controls. Two detectors, each exercised in both directions so a
broken detector cannot look reasonable.

A) The useTls guard at run-staged-g3-restart.ps1:515 (ticket 12's fix).
   RED     : unconditional assignment on the plaintext onboard config must throw
             SetValueInvocationException.
   GREEN   : the guarded form must not throw, and the written-back config must carry no useTls key.
   CONTROL : the same guarded form on a TLS-era onboard config must ENTER the branch and write
             useTls=false -- otherwise the guard would be vacuously green everywhere.

B) Get-SharedCommitBinding, the single binding source all three runners read.
   RED     : a doctored copy carrying the old TLS-era SHA must read back that old SHA
             (proves the reader reflects the file, not a baked-in constant).
   RED     : an uppercase SHA and a truncated SHA must each be rejected.
   GREEN   : the real file must read back the ticket's four values.
#>
param(
    [Parameter(Mandatory)][string]$OnboardSourceRepo,   # exact clone at 238b46e (read-only)
    [Parameter(Mandatory)][string]$TlsEraOnboardCommit, # e.g. 31263b1
    [Parameter(Mandatory)][string]$OutPath
)
$ErrorActionPreference = 'Stop'
$scripts = 'C:\Users\szy\Desktop\8005-agv-control-server\scripts'
$main = Join-Path $scripts 'run-staged-g3.ps1'
$restart = Join-Path $scripts 'run-staged-g3-restart.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("t13-red-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$configRelPath = 'src/SQCD.Agv.Wpf/appsettings.json'
$results = [System.Collections.Generic.List[object]]::new()
function Add-Row {
    param($Detector, $Direction, $Case, $Expected, $Observed, $Pass)
    $results.Add([ordered]@{
        detector = $Detector; direction = $Direction; case = $Case
        expected = $Expected; observed = $Observed; pass = $Pass
    })
}

# ---------------------------------------------------------------- A: the useTls guard
$plaintextJson = & git -C $OnboardSourceRepo show "238b46eb2c9ae90584e4288a782176f66b7de942:$configRelPath"
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the plaintext onboard appsettings.json' }
$tlsJson = & git -C $OnboardSourceRepo show "${TlsEraOnboardCommit}:$configRelPath"
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the TLS-era onboard appsettings.json' }

$plaintextHasUseTls = ((($plaintextJson -join "`n") | ConvertFrom-Json).wireToGate.PSObject.Properties.Name -contains 'useTls')
$tlsHasUseTls = ((($tlsJson -join "`n") | ConvertFrom-Json).wireToGate.PSObject.Properties.Name -contains 'useTls')
Add-Row 'useTls-guard' 'precondition' "plaintext 238b46e $configRelPath carries useTls" $false $plaintextHasUseTls ($plaintextHasUseTls -eq $false)
Add-Row 'useTls-guard' 'precondition' "TLS-era $TlsEraOnboardCommit $configRelPath carries useTls" $true $tlsHasUseTls ($tlsHasUseTls -eq $true)

# RED: unconditional assignment on the plaintext config
$redType = $null; $redMessage = $null
try {
    $s = ($plaintextJson -join "`n") | ConvertFrom-Json
    $s.wireToGate.useTls = $false
    $redType = '<no exception>'
}
catch { $redType = $_.Exception.GetType().Name; $redMessage = $_.Exception.Message }
Add-Row 'useTls-guard' 'RED' 'unconditional $settings.wireToGate.useTls = $false on plaintext config' `
    'SetValueInvocationException' $redType ($redType -eq 'SetValueInvocationException')

# GREEN: the guarded form, plus a readback of what actually got written
$greenType = '<no exception>'; $greenHasKey = $null
try {
    $s = ($plaintextJson -join "`n") | ConvertFrom-Json
    if ($s.wireToGate.PSObject.Properties.Name -contains 'useTls') { $s.wireToGate.useTls = $false }
    $out = Join-Path $work 'green-plaintext.json'
    $s | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $out -Encoding utf8NoBOM
    $greenHasKey = ((Get-Content -LiteralPath $out -Raw | ConvertFrom-Json).wireToGate.PSObject.Properties.Name -contains 'useTls')
}
catch { $greenType = $_.Exception.GetType().Name }
Add-Row 'useTls-guard' 'GREEN' 'guarded assignment on plaintext config throws nothing' `
    '<no exception>' $greenType ($greenType -eq '<no exception>')
Add-Row 'useTls-guard' 'GREEN' 'written-back plaintext config still carries no useTls key' `
    $false $greenHasKey ($greenHasKey -eq $false)

# CONTROL: the guard must actually fire on a config that has the key
$controlType = '<no exception>'; $controlValue = $null
try {
    $s = ($tlsJson -join "`n") | ConvertFrom-Json
    if ($s.wireToGate.PSObject.Properties.Name -contains 'useTls') { $s.wireToGate.useTls = $false }
    $out = Join-Path $work 'control-tls.json'
    $s | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $out -Encoding utf8NoBOM
    $controlValue = (Get-Content -LiteralPath $out -Raw | ConvertFrom-Json).wireToGate.useTls
}
catch { $controlType = $_.Exception.GetType().Name }
Add-Row 'useTls-guard' 'CONTROL' 'guarded assignment on TLS-era config enters the branch and writes false' `
    'False / <no exception>' "$controlValue / $controlType" (($controlValue -eq $false) -and ($controlType -eq '<no exception>'))

# ---------------------------------------------------------------- B: the binding reader
$pe = $null; $tk = $null
$restartAst = [System.Management.Automation.Language.Parser]::ParseFile($restart, [ref]$tk, [ref]$pe)
$fn = @($restartAst.FindAll({ param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq 'Get-SharedCommitBinding' }, $true))
Invoke-Expression $fn[0].Extent.Text

$expected = [ordered]@{
    ControlServerCommit = '56d4b1cc2f26325ca853acc4e7278bbde8651874'
    OnboardCommit       = '238b46eb2c9ae90584e4288a782176f66b7de942'
    SimulatorCommit     = 'fb5f7c593742bf98bc3957b8729a38aad5321f28'
    ProtocolCommit      = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279'
}
$live = Get-SharedCommitBinding -Path $main
foreach ($n in $expected.Keys) {
    Add-Row 'commit-binding-reader' 'GREEN' "live run-staged-g3.ps1 -> $n" $expected[$n] $live[$n] ($live[$n] -ceq $expected[$n])
}

$mainText = Get-Content -LiteralPath $main -Raw
$mutants = @(
    @{ name = 'old TLS-era ControlServerCommit'; text = $mainText.Replace('56d4b1cc2f26325ca853acc4e7278bbde8651874','3d8b00c7558ae700358f1f995a5ac75d12a3250c'); expect = 'reads-old-sha' }
    @{ name = 'uppercase ControlServerCommit';   text = $mainText.Replace('56d4b1cc2f26325ca853acc4e7278bbde8651874','56D4B1CC2F26325CA853ACC4E7278BBDE8651874'); expect = 'throws' }
    @{ name = 'truncated ControlServerCommit';   text = $mainText.Replace('56d4b1cc2f26325ca853acc4e7278bbde8651874','56d4b1cc'); expect = 'throws' }
)
foreach ($m in $mutants) {
    $p = Join-Path $work ("mutant-" + ($m.name -replace '\W','-') + '.ps1')
    Set-Content -LiteralPath $p -Value $m.text -Encoding utf8NoBOM
    $observed = $null
    try { $observed = (Get-SharedCommitBinding -Path $p)['ControlServerCommit'] }
    catch { $observed = 'throws: ' + $_.Exception.Message.Split("`n")[0] }
    $pass = if ($m.expect -eq 'throws') { $observed -like 'throws:*' }
            else { $observed -ceq '3d8b00c7558ae700358f1f995a5ac75d12a3250c' }
    Add-Row 'commit-binding-reader' 'RED' "mutant: $($m.name)" $m.expect $observed $pass
}

# ---------------------------------------------------------------- report
$report = [ordered]@{
    schemaVersion = '1.0.0'
    subject = 'ticket-13 red-side controls'
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    onboardSourceRepo = $OnboardSourceRepo
    onboardRepoWorktreeDirtyLines = @(& git -C $OnboardSourceRepo status --porcelain).Count
    bindingSource = 'scripts/run-staged-g3.ps1'
    bindingSourceSha256 = (Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash.ToLowerInvariant()
    guardSource = 'scripts/run-staged-g3-restart.ps1:515'
    guardSourceSha256 = (Get-FileHash -LiteralPath $restart -Algorithm SHA256).Hash.ToLowerInvariant()
    rows = @($results)
    failedRows = @($results | Where-Object { -not $_.pass })
    status = if (@($results | Where-Object { -not $_.pass }).Count -eq 0) { 'RED_SIDE_CONTROLS_PASS' } else { 'RED_SIDE_CONTROLS_FAIL' }
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutPath -Encoding utf8NoBOM
Remove-Item -LiteralPath $work -Recurse -Force

$results | ForEach-Object { [pscustomobject]$_ } |
    Format-Table detector,direction,case,expected,observed,pass -AutoSize |
    Out-String -Width 200 | Write-Output
Write-Output "STATUS: $($report.status)"
