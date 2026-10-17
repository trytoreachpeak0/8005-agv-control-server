#Requires -Version 7

<#
WIRE_TO_FULL_INSPECTION 绑定完备、走完一趟，AREA 端按侧取仓（control-server#737；program#195 决定 1、3、4）。

焊线/键合送全检：取货在焊线、键合机台（AREA 端），卸货在全检区（固定站，公共区域，同三光区一样送到区内即可）。
全检站还没加到 RIoT 26 号图上，所以这里是**模拟站点**：边车把本类绑到固定站 405「全检区」，站号与站名只写在 setup 里，
不进生产配置；现场绑定归 control-server#738。同挂机台站 12 的 N1-3 指 REAR、N1-7 指 FRONT。两条本类需求先后各走一趟：
在机台站 12 按需求 AREA 指派的那一组取货，到 405 卸货，需求结清。判据见 SameDirectionJourneyCommon.ps1，结清那一条另等
一次（README 第 14 条，Wait-L2ConditionOrLast）。

本类进 ExecutableTaskTypes 之前，这里 -01 读到的积压原因是本类不可受理，那就是本场景的红证据（见 PR）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SameDirectionJourneyCommon.ps1')

Invoke-L2SameDirectionJourneyScenario -Context $Context -TaskType 'WIRE_TO_FULL_INSPECTION' `
    -BoundStationRiotId 405 -BoundStationName '全检区' -IdPrefix 'L2-SDJ-WFI'
