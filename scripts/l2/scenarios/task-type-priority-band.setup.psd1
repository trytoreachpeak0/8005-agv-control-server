# 批次7-09（control-server#214），REQ-0202：STAGING_TO_WIRE 独占最高初始带。站点与两种任务类型的绑定照
# staging-to-wire-slot-group：机台站 12 同时挂着 N1-3 与 N1-7，WIRE_TO_GATE 从它取、送关卡 210，STAGING_TO_WIRE 从派工待送
# 取货站 305 取、送它。每区派车参数不写（默认「未配置」）：本区禁止途中追加，在途的车不会顺路接走积压里的需求，
# 车只有跑完手上这一趟才空出来——这正是要比「空出来之后先接谁」的局面。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '305' = '派工待送取货'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
        Bindings = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 305; StationName = '派工待送取货'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N1-7'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
