#Requires -Version 7
<#
control-server#71 验收探针（不入库）：AreaAssignments 覆盖默认表时，库里当前那一版分区归属表恰好是覆盖的两行，
不含默认表会有的 N1-7。经 FieldOps 的只读动词 area-assignments 读回，与现场核对的是同一条路。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assertions = $Context.Assertions

$table = & $Context.InvokeFieldOps -Arguments @('area-assignments')
$Context.Journal.Observe('probe:area-assignments', $table.outcome, @{ output = $table })
$actual = @($table.entries | ForEach-Object { "$($_.area)|$($_.dispatchZone)|$($_.slotPosition)" } | Sort-Object)
$expected = @('C15-13|MAP-25-WIRE_TO_GATE|FRONT', 'N1-3|MAP-25-WIRE_TO_GATE|REAR')
$assertions.Add('B4-07-OVR-01', '当前分区归属表是第 1 版，内容恰好是 setup 里覆盖的两行（N1-3 归 REAR，没有默认表的 N1-7）',
    ($table.outcome -eq 'OK' -and [int]$table.version -eq 1 -and ($actual -join ';') -eq ($expected -join ';')),
    "v1: $($expected -join '; ')", "v$($table.version): $($actual -join '; ')")

$versions = [int](Invoke-L2Query -Connection $Context.Connection -Sql 'SELECT COUNT(*) AS N FROM DispatchZoneAreaAssignmentVersions')[0].N
$assertions.Add('B4-07-OVR-02', '只导入了一版（没有先导默认表再导覆盖表）', ($versions -eq 1), 1, $versions)
