# 批次7-09（control-server#214），REQ-0202／REQ-0203／REQ-0210：防饥饿超时层与升级告警。站点与两种任务类型的绑定照
# staging-to-wire-slot-group：机台站 12 同时挂着 N1-3 与 N1-7，WIRE_TO_GATE 从它取、送关卡 210，STAGING_TO_WIRE 从派工待送
# 取货站 305 取、送它。每区派车参数开场不写（默认「未配置」）：第一遍看的正是阈值未批准时的降级——只计龄、不升级；
# 第二遍由场景自己经 FieldOps 导入 60 秒的阈值（正式导入动词，control-server#216），不走 setup 的 L2 预置，因为导入必须
# 发生在服务端已经跑着、需求已经在等的时候。本区禁止途中追加：阈值那一列导入时途中追加那一列留空。
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
