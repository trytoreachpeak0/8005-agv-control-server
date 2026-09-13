#Requires -Version 7
<#
.SYNOPSIS
    Reads a vehicle's IO module once, fires nothing, and says whether the image is what an idle vehicle
    with every slot closed and empty reads under ticket 35.

.DESCRIPTION
    The check before Invoke-W1SlotIoProbe.ps1, and the way to confirm that an -IoModuleHost measured on
    the day is the module bolted to this vehicle before anything is fired at it.

    Ticket 35: every unlock output 0, every lock feedback 1 (locked), every light curtain 1 (empty).
    Exit 0 when the image reads exactly that, 1 when it does not -- a slot left open or holding
    something is a reason to go and look, not an error in the check. Exit 2 when the module or the
    vehicle cannot be reached, or the onboard client or the slots simulator is running.

.EXAMPLE
    pwsh -File scripts/field/Test-W1SlotIoModule.ps1 -SiteAlias agv02 -IoModuleHost 192.168.71.50
#>
[CmdletBinding(DefaultParameterSetName = 'Ssh')]
param(
    [Parameter(ParameterSetName = 'Ssh', Mandatory)][string]$SiteAlias,
    [Parameter(ParameterSetName = 'Ssh', Mandatory)][string]$IoModuleHost,

    [Parameter(ParameterSetName = 'Local', Mandatory)][switch]$Local,
    [Parameter(ParameterSetName = 'Local')][string]$LocalHost = '127.0.0.1',

    [int]$Port = 502,
    [int]$UnitId = 255,
    [int]$DoStartAddress = 100,
    [int]$DiStartAddress = 200,
    [int]$ChannelCount = 16
)

$ErrorActionPreference = 'Stop'
$slotCount = 8

$library = Join-Path $PSScriptRoot 'W1SlotIo.ps1'
$endpoint = @{
    HostName = $Local ? $LocalHost : $IoModuleHost
    Port     = $Port
    UnitId   = $UnitId
    DoStart  = $DoStartAddress
    DiStart  = $DiStartAddress
    Channels = $ChannelCount
}

try {
    if ($Local) {
        . $library
        $blocking = @()
        $image = Invoke-W1IoOperation -Endpoint $endpoint -Operation image
    }
    else {
        $libraryText = (Get-Content -LiteralPath $library -Raw) -replace '(?s)<#.*?#>', '' -replace '(?m)^\s*#.*$', ''
        $endpointJson = ($endpoint | ConvertTo-Json -Compress).Replace("'", "''")
        $script = @"
$libraryText
`$ErrorActionPreference = 'Stop'
`$blocking = @(Get-Process -Name 'SQCD.Agv.Wpf', 'SQCD_8005AGV_Simulator' -ErrorAction SilentlyContinue | ForEach-Object { `$_.ProcessName })
`$image = if (`$blocking.Count -eq 0) { Invoke-W1IoOperation -Endpoint ('$endpointJson' | ConvertFrom-Json -AsHashtable) -Operation image } else { `$null }
[ordered]@{ blocking = `$blocking; image = `$image } | ConvertTo-Json -Depth 8 -Compress
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $output = & ssh -o BatchMode=yes -o ConnectTimeout=15 $SiteAlias "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "ssh $SiteAlias failed: $(($output | Out-String).Trim())" }
        $json = @($output | ForEach-Object { "$_" } | Where-Object { $_.StartsWith('{') }) | Select-Object -Last 1
        if (-not $json) { throw "ssh $SiteAlias returned no JSON: $(($output | Out-String).Trim())" }
        $reply = $json | ConvertFrom-Json -AsHashtable
        $blocking = @($reply.blocking)
        $image = $reply.image
    }
}
catch {
    Write-Host "无法读取模块：$($_.Exception.Message)"
    exit 2
}

if ($blocking.Count -gt 0) {
    Write-Host "车上正在运行 $($blocking -join '、')，先从车上自己的桌面关掉，再读模块。"
    exit 2
}

$outputs = @($image.before.do)
$locks = @($image.before.di | Select-Object -First $slotCount)
$curtains = @($image.before.di | Select-Object -Skip $slotCount -First $slotCount)

$outputsIdle = @($outputs | Where-Object { $_ -ne 0 }).Count -eq 0
$notLocked = @(1..$slotCount | Where-Object { $locks[$_ - 1] -ne 1 })
$notEmpty = @(1..$slotCount | Where-Object { $curtains[$_ - 1] -ne 1 })

$target = $Local ? "$LocalHost`:$Port" : "$SiteAlias -> $IoModuleHost`:$Port"
Write-Host "模块 $target（unitId $UnitId，DO 起始 $DoStartAddress，DI 起始 $DiStartAddress）"
Write-Host "  DO1-16  开锁输出（0=空闲）：$($outputs -join '')"
Write-Host "  DI1-8   锁反馈（1=锁闭）：  $($locks -join '')"
Write-Host "  DI9-16  光幕（1=无物）：    $($curtains -join '')"

if ($outputsIdle -and $notLocked.Count -eq 0 -and $notEmpty.Count -eq 0) {
    Write-Host '结论：输出全空闲、八仓都锁闭、八仓都无物，可以开始逐仓核对。'
    exit 0
}

$problems = @()
if (-not $outputsIdle) { $problems += '有开锁输出处于置位' }
if ($notLocked.Count) { $problems += "未锁闭的仓：$($notLocked -join '、')" }
if ($notEmpty.Count) { $problems += "光幕读到有物的仓：$($notEmpty -join '、')" }
Write-Host "结论：先去车前看一眼再开始——$($problems -join '；')。"
exit 1
