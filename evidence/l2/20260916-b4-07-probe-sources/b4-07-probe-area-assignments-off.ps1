#Requires -Version 7
<#
control-server#71 验收探针（不入库）：AreaAssignments = $false 时默认前置不导入分区归属表，入库与绑定照做。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$connection = $Context.Connection
$assertions = $Context.Assertions

$versions = [int](Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM DispatchZoneAreaAssignmentVersions')[0].N
$assertions.Add('B4-07-OFF-01', 'AreaAssignments = $false：库里一版分区归属表都没有', ($versions -eq 0), 0, $versions)

$bindings = [int](Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM SlotIoBindings WHERE AgvId = '$($Context.AgvId)' AND Status = 'PUBLISHED'")[0].N
$assertions.Add('B4-07-OFF-02', '入库与绑定照做：主车有 8 行已发布 IO 绑定，且 Context.SlotModelVersionId 已给出',
    ($bindings -eq 8 -and -not [string]::IsNullOrWhiteSpace($Context.SlotModelVersionId)), '8 / 非空', "$bindings / $($Context.SlotModelVersionId)")

$timeline = @(Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'timeline.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$criteria = @($timeline | Where-Object { $_.PSObject.Properties.Name -contains 'criterion' } | ForEach-Object { [string]$_.criterion })
$assertions.Add('B4-07-OFF-03', '时间线里有入库与绑定两步，没有导入那一步',
    ('slot-model-preseed:seed-approved-facts' -in $criteria -and "slot-model-preseed:bind-io:$($Context.AgvId)" -in $criteria -and
     'slot-model-preseed:import-area-assignments' -notin $criteria),
    'seed + bind，无 import', (($criteria | Where-Object { $_ -like 'slot-model-preseed:*' }) -join ', '))
