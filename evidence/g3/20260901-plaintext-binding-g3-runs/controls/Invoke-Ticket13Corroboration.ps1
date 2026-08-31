#requires -Version 7
<#
The three runners write tls = $false / temporaryTrustRootInstalled = $false /
certificatesGenerated = $false as literals. Ticket 13 asks for those to be read back rather
than trusted, so corroborate each one against something the runners did not author.
#>
param([Parameter(Mandatory)][string]$BeforeSnapshot, [Parameter(Mandatory)][string]$OutPath)
$ErrorActionPreference = 'Stop'

$stageRoots = @(
    'C:\Users\szy\w2g-ticket13\stage-restart'
    'C:\Users\szy\w2g-ticket13\stage-main'
    'C:\Users\szy\w2g-ticket13\stage-vectors')
$evidenceRoots = @(
    'C:\Users\szy\w2g-ticket13\evidence-restart'
    'C:\Users\szy\w2g-ticket13\evidence-main'
    'C:\Users\szy\w2g-ticket13\evidence-vectors')

$rows = [System.Collections.Generic.List[object]]::new()
function Add-Row { param($Claim, $Method, $Expected, $Observed, $Pass)
    $rows.Add([ordered]@{ claim = $Claim; method = $Method; expected = $Expected; observed = $Observed; pass = $Pass }) }

# --- 1. certificate stores unchanged across all three runs ---
$before = Get-Content -LiteralPath $BeforeSnapshot -Raw | ConvertFrom-Json
foreach ($s in @('CurrentUser\Root','CurrentUser\My','LocalMachine\Root','LocalMachine\My')) {
    $items = @(Get-ChildItem -Path "Cert:\$s" -ErrorAction SilentlyContinue)
    $afterDigest = [BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes((($items.Thumbprint | Sort-Object) -join ',')))
        ).Replace('-','').ToLowerInvariant()
    $b = $before.stores.$s
    Add-Row "no certificate installed or removed in $s" 'thumbprint-set digest before vs after' `
        "$($b.count) / $($b.thumbprintsSha256.Substring(0,16))" `
        "$($items.Count) / $($afterDigest.Substring(0,16))" `
        (($items.Count -eq $b.count) -and ($afterDigest -ceq $b.thumbprintsSha256))
}

# --- 2. no key material anywhere the runners wrote ---
$patterns = @('*.pfx','*.p12','*.cer','*.crt','*.key','*.pem','*.der')
foreach ($root in ($stageRoots + $evidenceRoots)) {
    $hits = @(Get-ChildItem -LiteralPath $root -Recurse -File -Include $patterns -ErrorAction SilentlyContinue)
    Add-Row "no key material under $(Split-Path -Leaf $root)" "recursive scan for $($patterns -join ',')" `
        0 $hits.Count ($hits.Count -eq 0)
    $certsDirs = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Filter 'certs' -ErrorAction SilentlyContinue)
    Add-Row "no certs\ directory under $(Split-Path -Leaf $root)" 'recursive directory scan' `
        0 $certsDirs.Count ($certsDirs.Count -eq 0)
}

# --- 3. the onboard config each runner actually published carries no TLS surface ---
foreach ($root in $stageRoots) {
    $cfg = Join-Path $root 'publish\onboard-hmi\appsettings.json'
    if (-not (Test-Path -LiteralPath $cfg)) {
        # run-demand-bearing-g3-vectors.ps1 drives the server with the synthetic peer harness it
        # extracts from run-staged-g3.ps1; it never clones or publishes the onboard app. Assert that
        # design fact rather than a missing file, so an onboard peer appearing here would show up red.
        # @(...).Name on a one-element result yields a bare string, whose [0] is its first CHARACTER.
        # Wrap the projection, not the source, or the single-directory case compares 'c' to 'control-server'.
        $publishedProjects = @((Get-ChildItem -LiteralPath (Join-Path $root 'publish') -Directory -ErrorAction SilentlyContinue).Name)
        Add-Row "$(Split-Path -Leaf $root) publishes no onboard peer (synthetic harness only)" `
            'list publish\ subdirectories' 'control-server' ($publishedProjects -join ',') `
            (($publishedProjects.Count -eq 1) -and ($publishedProjects[0] -eq 'control-server'))
        continue
    }
    $w = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).wireToGate
    $names = @($w.PSObject.Properties.Name)
    $tlsKeys = @($names | Where-Object { $_ -in @('useTls','serverCertificateSha256','serverCertificatePath') })
    Add-Row "published onboard config under $(Split-Path -Leaf $root) has no TLS keys" `
        'read back publish\onboard-hmi\appsettings.json' '<none>' ($tlsKeys -join ',' | ForEach-Object { if ($_) { $_ } else { '<none>' } }) ($tlsKeys.Count -eq 0)
}

# --- 4. no runner emitted a TLS/Schannel/certificate diagnostic ---
$logHits = [System.Collections.Generic.List[object]]::new()
foreach ($root in $evidenceRoots) {
    foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $root 'logs') -File -ErrorAction SilentlyContinue)) {
        $m = @(Select-String -LiteralPath $f.FullName -Pattern 'Schannel|SslStream|AuthenticationException|X509|https://127\.0\.0\.1|certificate' -AllMatches -ErrorAction SilentlyContinue)
        if ($m.Count -gt 0) { $logHits.Add([ordered]@{ file = $f.FullName; matches = $m.Count; sample = $m[0].Line.Trim() }) }
    }
}
Add-Row 'no TLS/certificate diagnostic in any runner log' `
    'grep Schannel|SslStream|AuthenticationException|X509|https://127.0.0.1|certificate over all logs' `
    0 $logHits.Count ($logHits.Count -eq 0)

# --- 5. the production service was not disturbed ---
$svc = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer'" -ErrorAction SilentlyContinue
$proc = if ($svc -and $svc.ProcessId) { Get-Process -Id $svc.ProcessId -ErrorAction SilentlyContinue } else { $null }
Add-Row 'production service still running on its original PID' 'Win32_Service ProcessId' `
    '8632 / Running' "$($svc.ProcessId) / $($svc.State)" (($svc.ProcessId -eq 8632) -and ($svc.State -eq 'Running'))
Add-Row 'production service process was never restarted' 'process start time predates the runs' `
    '< 2026-09-01 (this session)' ($(if ($proc) { $proc.StartTime.ToString('o') } else { '<no process>' })) `
    ($null -ne $proc -and $proc.StartTime -lt (Get-Date).AddHours(-1))

# --- 6. the read-only onboard repository took zero writes ---
$onboardRepo = 'C:\Users\szy\Desktop\8005-agv-onboard-hmi'
$dirty = @(& git -C $onboardRepo status --porcelain)
Add-Row 'read-only onboard repository has zero tracked changes' 'git status --porcelain' 0 $dirty.Count ($dirty.Count -eq 0)
$onboardHead = (& git -C $onboardRepo rev-parse HEAD).Trim()
Add-Row 'read-only onboard repository HEAD unmoved' 'git rev-parse HEAD' 'bbfbc52…' $onboardHead ($onboardHead -like 'bbfbc52*')

$report = [ordered]@{
    schemaVersion = '1.0.0'
    subject = 'ticket-13 corroboration of the literal no-TLS evidence fields'
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    certificateStoreSnapshotBefore = $BeforeSnapshot
    logHits = @($logHits)
    rows = @($rows)
    failedRows = @($rows | Where-Object { -not $_.pass })
    status = if (@($rows | Where-Object { -not $_.pass }).Count -eq 0) { 'CORROBORATION_PASS' } else { 'CORROBORATION_FAIL' }
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutPath -Encoding utf8NoBOM
$rows | ForEach-Object { [pscustomobject]$_ } | Format-Table claim,expected,observed,pass -AutoSize |
    Out-String -Width 200 | Write-Output
if ($logHits.Count -gt 0) { Write-Output "LOG HITS:"; $logHits | ForEach-Object { Write-Output "  $($_.file) x$($_.matches): $($_.sample)" } }
Write-Output "STATUS: $($report.status)"
