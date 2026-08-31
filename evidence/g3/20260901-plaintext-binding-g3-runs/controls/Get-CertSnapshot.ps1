#requires -Version 7
param([Parameter(Mandatory)][string]$OutPath, [Parameter(Mandatory)][string]$Label)
$ErrorActionPreference = 'Stop'
$stores = [ordered]@{}
foreach ($s in @('CurrentUser\Root','CurrentUser\My','LocalMachine\Root','LocalMachine\My')) {
    $items = @(Get-ChildItem -Path "Cert:\$s" -ErrorAction SilentlyContinue)
    $stores[$s] = [ordered]@{
        count = $items.Count
        thumbprintsSha256 = (
            [BitConverter]::ToString(
                [System.Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes((($items.Thumbprint | Sort-Object) -join ',')))
            ).Replace('-','').ToLowerInvariant())
        thumbprints = @($items.Thumbprint | Sort-Object)
    }
}
$snapshot = [ordered]@{
    label = $Label
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    machine = $env:COMPUTERNAME
    stores = $stores
}
$snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutPath -Encoding utf8NoBOM
"$Label : " + (($stores.Keys | ForEach-Object { "$_=$($stores[$_].count)" }) -join '  ')
