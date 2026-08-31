#requires -Version 7
# Ticket 03 probe. Runs unelevated: proves the configuration the install chain now emits starts the
# plaintext host, and that the upgrade migration is load-bearing rather than decorative.
# Isolated ports and an isolated SQLite file; the installed production service is not touched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Repository
)

$ErrorActionPreference = 'Stop'
$publish = Join-Path $Root 'publish'
$evidence = Join-Path $Root 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

$onboardPort = 58305
$healthPort = 58307

function Get-ScriptElement {
    param([string]$Path, [string]$Text, [string]$Kind)
    $errors = $null; $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Parse errors in $Path" }
    if ($Kind -eq 'function') {
        $found = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Text }, $true))
    } else {
        $found = @($ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $n.Left.Extent.Text -eq $Text
        }, $true))
    }
    if ($found.Count -ne 1) { throw "Expected exactly one '$Text' in $Path, found $($found.Count)" }
    return $found[0].Extent.Text
}

function Get-ScriptElementFromText {
    param([string]$Content, [string]$Text)
    $temporary = Join-Path $Root 'baseline-install.ps1'
    [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
    return Get-ScriptElement -Path $temporary -Text $Text -Kind 'assignment'
}

function Start-Host {
    param([string]$ContentRoot, [string]$Tag)
    $out = Join-Path $evidence "$Tag.out.log"
    $err = Join-Path $evidence "$Tag.err.log"
    $environment = @{
        DOTNET_ENVIRONMENT = 'Production'
        CONTROL_SERVER_ONBOARD_CREDENTIAL = 'ticket03-probe-credential'
        CONTROL_SERVER_RIOT_CALL_API_KEY = 'ticket03-probe-riot'
        MesIngest__baseUrl = 'http://127.0.0.1:1'
        RIoT__baseUrl = 'http://127.0.0.1:1'
    }
    return Start-Process -FilePath (Join-Path $publish 'ControlServer.Host.exe') `
        -ArgumentList @('--contentRoot', $ContentRoot, '--environment', 'Production') `
        -WorkingDirectory $ContentRoot -RedirectStandardOutput $out -RedirectStandardError $err `
        -Environment $environment -WindowStyle Hidden -PassThru
}

function Wait-Live {
    param([int]$TimeoutSeconds = 30)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $body = & "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error `
                --noproxy 127.0.0.1 --max-time 3 "http://127.0.0.1:$healthPort/health/live" 2>&1
            if ($LASTEXITCODE -eq 0) { return ($body | ConvertFrom-Json) }
        } catch { }
        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $null
}

function New-ContentRoot {
    param([string]$Name, [string]$ConfigurationJson)
    $target = Join-Path $Root $Name
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force
    [IO.File]::WriteAllText(
        (Join-Path $target 'appsettings.Production.json'),
        $ConfigurationJson,
        [Text.UTF8Encoding]::new($false))
    return $target
}

$report = [ordered]@{}

# ---- Case 1: the configuration Install-ControlServerLocal.ps1 now writes --------------------
$installScript = Join-Path $Repository 'scripts\Install-ControlServerLocal.ps1'
$literal = Get-ScriptElement -Path $installScript -Text '$configuration' -Kind 'assignment'
$dataRoot = Join-Path $Root 'case1-data'
$logDirectory = Join-Path $dataRoot 'logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$databasePath = Join-Path (Join-Path $dataRoot 'data') 'controlserver.db'
New-Item -ItemType Directory -Path (Split-Path -Parent $databasePath) -Force | Out-Null
$ListenAddress = '127.0.0.1'
$healthOrigin = "http://127.0.0.1:$healthPort"
$OnboardPort = $onboardPort
Invoke-Expression $literal
$installJson = $configuration | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $evidence 'install-emitted-appsettings.Production.json'), $installJson, [Text.UTF8Encoding]::new($false))

$case1Root = New-ContentRoot -Name 'case1' -ConfigurationJson $installJson
$process = Start-Host -ContentRoot $case1Root -Tag 'case1-install-shape'
$live = Wait-Live
$listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue |
    ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" } | Sort-Object -Unique)
$startupLog = Get-Content -Raw -LiteralPath (Join-Path $evidence 'case1-install-shape.out.log') -ErrorAction SilentlyContinue
$report['case1_installEmittedConfigurationStarts'] = [ordered]@{
    liveStatus = $live.status
    listeners = $listeners
    transportLogLine = @($startupLog -split "`r?`n" | Where-Object { $_ -match 'Onboard NDJSON listener' })
    certificateWordsInStartupLog = @($startupLog -split "`r?`n" | Where-Object { $_ -match '(?i)certificate|tls|https' })
}
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
$process.WaitForExit(10000) | Out-Null

# ---- Case 2 (RED): a TLS-era configuration carried forward unmigrated ------------------------
$baselineText = & git -C $Repository show 'e5ee065:scripts/Install-ControlServerLocal.ps1'
$baselineLiteral = Get-ScriptElementFromText -Content ($baselineText -join "`n") -Text '$configuration'
$certificatePath = Join-Path $dataRoot 'certs\localhost.pfx'
$healthOrigin = "https://localhost:$healthPort"
Remove-Variable -Name configuration -ErrorAction SilentlyContinue
Invoke-Expression $baselineLiteral
$legacyJson = $configuration | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $evidence 'legacy-appsettings.Production.json'), $legacyJson, [Text.UTF8Encoding]::new($false))

$case2Root = New-ContentRoot -Name 'case2' -ConfigurationJson $legacyJson
$process = Start-Host -ContentRoot $case2Root -Tag 'case2-legacy-red'
$live = Wait-Live -TimeoutSeconds 12
$process.WaitForExit(15000) | Out-Null
$case2Err = Get-Content -Raw -LiteralPath (Join-Path $evidence 'case2-legacy-red.err.log') -ErrorAction SilentlyContinue
$case2Out = Get-Content -Raw -LiteralPath (Join-Path $evidence 'case2-legacy-red.out.log') -ErrorAction SilentlyContinue
$report['case2_unmigratedLegacyConfigurationIsRefused'] = [ordered]@{
    liveStatus = $live.status
    processExited = $process.HasExited
    exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    refusalLines = @(("$case2Out`n$case2Err") -split "`r?`n" | Where-Object { $_ -match 'was removed in this version' })
}
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue

# ---- Case 3 (GREEN): the same file after the upgrade migration -------------------------------
$updateScript = Join-Path $Repository 'scripts\Update-ControlServerLocal.ps1'
Invoke-Expression (Get-ScriptElement -Path $updateScript -Text 'Convert-RetainedConfigurationToPlaintext' -Kind 'function')
$migrationTarget = Join-Path $Root 'migrated-appsettings.Production.json'
[IO.File]::WriteAllText($migrationTarget, $legacyJson, [Text.UTF8Encoding]::new($false))
$migration = Convert-RetainedConfigurationToPlaintext $migrationTarget
$migratedJson = Get-Content -Raw -LiteralPath $migrationTarget
Copy-Item -LiteralPath $migrationTarget -Destination (Join-Path $evidence 'migrated-appsettings.Production.json') -Force

$case3Root = New-ContentRoot -Name 'case3' -ConfigurationJson $migratedJson
$process = Start-Host -ContentRoot $case3Root -Tag 'case3-migrated-green'
$live = Wait-Live
$startupLog = Get-Content -Raw -LiteralPath (Join-Path $evidence 'case3-migrated-green.out.log') -ErrorAction SilentlyContinue
$report['case3_migratedLegacyConfigurationStarts'] = [ordered]@{
    removedKeys = @($migration.removedKeys)
    healthUrlRewritten = $migration.healthUrlRewritten
    healthUrl = $migration.healthUrl
    liveStatus = $live.status
    transportLogLine = @($startupLog -split "`r?`n" | Where-Object { $_ -match 'Onboard NDJSON listener' })
    residualCertificateKeys = @(($migratedJson | ConvertFrom-Json).OnboardTransport.PSObject.Properties.Name |
        Where-Object { $_ -match '(?i)certificate|insecure' })
}
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
$process.WaitForExit(10000) | Out-Null

$reportJson = $report | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $evidence 'probe-report.json'), $reportJson, [Text.UTF8Encoding]::new($false))
$reportJson
