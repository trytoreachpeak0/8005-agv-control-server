#requires -Version 7
<#
.SYNOPSIS
The detectors ticket 09's acceptance run and its red side both use.

.DESCRIPTION
These live in one module so that the red side exercises the same code the acceptance run relies on.
A red side that re-declares its own copy of a detector proves only that the copy works, which is the
failure mode ticket 08's symbol probe hit from the other direction: a detector that could not report
ABSENT reported PRESENT for everything and nobody noticed until an impossible all-green appeared.

Every detector here returns an observation, never a verdict. The verdict, its expectation and its
control belong to the calling run.
#>

# A count of certificates is not enough: two stores can hold the same number of different
# certificates. The observation is a digest over the sorted thumbprint set.
function Get-StoreDigest {
    param([Parameter(Mandatory)][string]$StoreName, [Parameter(Mandatory)][string]$Location)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $Location)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $thumbprints = @(@($store.Certificates | ForEach-Object { $_.Thumbprint }) | Sort-Object)
        return (New-StoreDigest "$Location\$StoreName" $thumbprints)
    }
    finally { $store.Close(); $store.Dispose() }
}

# Split out so the red side can build a digest from a mutated thumbprint set without needing a
# certificate store it is allowed to write to.
function New-StoreDigest {
    param([Parameter(Mandatory)][string]$Store, [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Thumbprints)
    $sorted = @(@($Thumbprints) | Sort-Object)
    return [ordered]@{
        store = $Store
        count = $sorted.Count
        digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::ASCII.GetBytes($sorted -join ','))).ToLowerInvariant()
    }
}

function Get-AllStoreDigests {
    return @((Get-StoreDigest 'Root' 'CurrentUser'), (Get-StoreDigest 'Root' 'LocalMachine'),
             (Get-StoreDigest 'My' 'CurrentUser'), (Get-StoreDigest 'My' 'LocalMachine'))
}

function Compare-StoreDigests {
    param([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After)
    $changed = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt @($Before).Count; $i++) {
        $before = @($Before)[$i]
        $after = @($After)[$i]
        if ($before.digest -ne $after.digest) {
            # The formatted string is built into a variable first on purpose. Inside a method-call
            # argument list the commas bind to the CALL, not to -f, so `.Add('{0}..{4}' -f a,b,c,d,e)`
            # hands -f a single argument, throws on {1}, and the Add never runs -- a detector that can
            # never report a difference. This one was caught by the red side, not by review.
            $line = '{0} {1}->{2} ({3}->{4} certs)' -f $before.store,
                $before.digest.Substring(0, 12), $after.digest.Substring(0, 12),
                $before.count, $after.count
            $changed.Add($line)
        }
    }
    return @($changed)
}

# Ticket 03's snapshot took listeners by process NAME, so an isolated probe running the same
# executable folded its own ports into the production reading. This one filters by service PID.
function Get-ProductionSnapshot {
    param([string]$ServiceName = '8005 AGV ControlServer')
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $processIds = @()
    if ($service) {
        $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
        if ($wmi -and $wmi.ProcessId -gt 0) { $processIds = @([int]$wmi.ProcessId) }
    }
    $listeners = @()
    foreach ($processId in $processIds) {
        $listeners += @(Get-NetTCPConnection -State Listen -OwningProcess $processId -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}:{1}' -f $_.LocalAddress, $_.LocalPort })
    }
    return [ordered]@{
        status = if ($service) { $service.Status.ToString() } else { $null }
        processIds = @($processIds)
        listeners = @(@($listeners) | Sort-Object -Unique)
    }
}

function Compare-ProductionSnapshot {
    param([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After)
    $differences = [System.Collections.Generic.List[string]]::new()
    if ($Before.status -ne $After.status) { $differences.Add("status:$($Before.status)->$($After.status)") }
    if (($Before.processIds -join ',') -ne ($After.processIds -join ',')) {
        $differences.Add("pid:$($Before.processIds -join ',')->$($After.processIds -join ',')")
    }
    if (($Before.listeners -join ' ') -ne ($After.listeners -join ' ')) {
        $differences.Add("listeners:$($Before.listeners -join ' ')->$($After.listeners -join ' ')")
    }
    return @($differences)
}

function Get-KeyMaterialFiles {
    param([Parameter(Mandatory)][string[]]$Path)
    $existing = @($Path | Where-Object { Test-Path -LiteralPath $_ })
    if ($existing.Count -eq 0) { return @() }
    # The projection stays in the pipeline. @(<empty>.FullName) is @($null), whose Count is 1, so a
    # detector written that way reports one nameless finding when there is nothing to find; and on a
    # single-element result @(...).FullName degrades to one string whose [0] is a character. Both
    # traps disappear once ForEach-Object does the projection.
    return @(Get-ChildItem -LiteralPath $existing -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.pfx', '.pem', '.cer', '.crt', '.p12', '.key') } |
        ForEach-Object { $_.FullName })
}

# The four configuration keys the plaintext product rejects at startup. Matched by NAME against the
# file text: the correct criterion for a removed key is that the name appears at all, not that its
# value happens to be the one the new binary would have wanted.
function Get-RemovedCertificateKeys {
    param([Parameter(Mandatory)][string]$ConfigurationPath)
    $text = Get-Content -Raw -LiteralPath $ConfigurationPath
    $names = @('serverCertificatePath', 'serverCertificatePasswordEnvironmentVariable',
        'allowInsecureLoopback', 'requireHttps')
    return @($names | Where-Object { $text -match [regex]::Escape($_) })
}

function Get-OnboardTlsKeys {
    param([Parameter(Mandatory)][string]$ConfigurationPath)
    $text = Get-Content -Raw -LiteralPath $ConfigurationPath
    $names = @('useTls', 'serverCertificateSha256', 'requireHttps')
    return @($names | Where-Object { $text -match [regex]::Escape($_) })
}

function Get-TreeHashes {
    param([Parameter(Mandatory)][string]$Path)
    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName)) {
        $map[$file.FullName.Substring($prefix.Length).TrimStart('\')] =
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Compare-TreeHashes {
    param([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After)
    $differences = [System.Collections.Generic.List[string]]::new()
    foreach ($key in $Before.Keys) {
        if (-not $After.Contains($key)) { $differences.Add("missing:$key") }
        elseif ($After[$key] -ne $Before[$key]) { $differences.Add("changed:$key") }
    }
    foreach ($key in $After.Keys) {
        if (-not $Before.Contains($key)) { $differences.Add("added:$key") }
    }
    return @($differences)
}

# Section 3 of the manual, run over every listed file. Returns the files that did not verify.
function Get-HashMismatches {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$SumsPath)
    $mismatches = [System.Collections.Generic.List[string]]::new()
    $checked = 0
    Push-Location -LiteralPath $Root
    try {
        foreach ($line in (Get-Content -LiteralPath $SumsPath)) {
            $parts = $line -split '  ', 2
            if ($parts.Count -ne 2) { continue }
            $checked++
            if (-not (Test-Path -LiteralPath $parts[1] -PathType Leaf)) { $mismatches.Add($parts[1]); continue }
            $actual = (Get-FileHash -LiteralPath $parts[1] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $parts[0]) { $mismatches.Add($parts[1]) }
        }
    }
    finally { Pop-Location }
    return [ordered]@{ checked = $checked; mismatches = @($mismatches) }
}

Export-ModuleMember -Function Get-StoreDigest, New-StoreDigest, Get-AllStoreDigests, Compare-StoreDigests,
    Get-ProductionSnapshot, Compare-ProductionSnapshot, Get-KeyMaterialFiles, Get-RemovedCertificateKeys,
    Get-OnboardTlsKeys, Get-TreeHashes, Compare-TreeHashes, Get-HashMismatches
