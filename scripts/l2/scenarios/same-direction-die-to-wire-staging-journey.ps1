#Requires -Version 7

<#
DIE_TO_WIRE_STAGING 绑定完备、走完一趟，AREA 端按侧取仓（批次10-02，control-server#546；规格 8.3 批次 10，REQ-0184、REQ-0335、REQ-0334）。

粘片到焊线派工待送：取货在粘片区机台（AREA 端），卸货在派工待送卸货点（固定站）。
边车把本类绑到固定站 401「派工待送卸货」，同挂机台站 12 的 N1-3 指 REAR、N1-7 指 FRONT。两条本类需求先后各走一趟：
在机台站 12 按需求 AREA 指派的那一组取货，到 401 卸货，需求结清。判据见 SameDirectionJourneyCommon.ps1。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SameDirectionJourneyCommon.ps1')

Invoke-L2SameDirectionJourneyScenario -Context $Context -TaskType 'DIE_TO_WIRE_STAGING' `
    -BoundStationRiotId 401 -BoundStationName '派工待送卸货' -IdPrefix 'L2-SDJ-DWS'
