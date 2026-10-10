#Requires -Version 7
# Runs the control host's post-install check command locally, built exactly as
# 19-deploy-control-server-parallel.ps1 builds it, against a temporary "ops root" holding a copy
# of the module. Proves the quoting survives -EncodedCommand and that the three outcomes print
# what the control host expects.
param([Parameter(Mandatory)][string] $ModulePath)
$ErrorActionPreference = 'Stop'
$opsRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-ops-v2-$PID"
New-Item -ItemType Directory -Path $opsRoot | Out-Null
try {
    Copy-Item -LiteralPath $ModulePath -Destination $opsRoot
    function Invoke-Check([string] $remoteConfig) {
        $check = @(
            "Import-Module '$opsRoot\ParallelInstance.psm1' -Force"
            "`$r = Remove-ParallelInstanceDeploymentConfig -Path '$remoteConfig' -FallbackDirectory '$opsRoot'"
            "if (`$r) { 'SECRET_FILE_LEFT_BEHIND: $remoteConfig ' + `$r; exit 3 }"
            "'deploy-config.json: not on the server'"
        ) -join '; '
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($check))
        $out = @(& pwsh -NoProfile -EncodedCommand $encoded 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join ' / ') }
    }
    $config = Join-Path $opsRoot 'deploy-config.json'
    Set-Content -LiteralPath $config -Value '{}'
    $a = Invoke-Check $config
    "1. file in the ops root:     exit=$($a.Exit) gone=$(-not (Test-Path -LiteralPath $config)) | $($a.Output)"
    $b = Invoke-Check $config
    "2. file already gone:        exit=$($b.Exit) | $($b.Output)"
    $other = Join-Path $opsRoot 'sub'
    New-Item -ItemType Directory -Path $other | Out-Null
    $otherFile = Join-Path $other 'deploy-config.json'
    Set-Content -LiteralPath $otherFile -Value '{}'
    $c = Invoke-Check $otherFile
    "3. file somewhere else:      exit=$($c.Exit) kept=$(Test-Path -LiteralPath $otherFile) | $($c.Output)"
} finally {
    Remove-Item -LiteralPath $opsRoot -Recurse -Force
}
