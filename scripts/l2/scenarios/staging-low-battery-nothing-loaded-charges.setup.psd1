# program#184 第一波集成验证必测清单第 ① 条：待送点缺电、一篮没装 → 旅程原地收尾 → 车成空闲车（control-server#712 规则下不空闲返回）
# → 进充电队列 → 被派去充电。
#
# 装置照 staging-only-vehicle-stays-at-staging-origin（control-server#712）：两台合成车，车队配置里都只接 STAGING_TO_WIRE，空闲返回打开，
# 两个等待点都在路网上——生产上 agv02 就是这样一台车。多出来的只有充电桩：站 211「充电点1」放在假地图节点 6（与等待点 214 同一个节点，
# control-server#431 起允许），场景自己把它登记到假 RIoT、经 FieldOps 导入名册（L2Chargers.psm1）。
#
# 待送点规则打开（服务端出厂是关的；control-server#709 的缺电收尾只在开关打开时有事可做）。站点离站期限拉到十分钟：
# 收尾必须来自缺电，而不是期限到了（待送点上本来也不计期限，这里是双保险）。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    FleetAllowedTaskTypes = @('STAGING_TO_WIRE')
    IdleReturn = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 6 },
        @{ StationId = 215; StationName = '等待点2'; Node = 4 }
    )
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    StagingDepartureRulesEnabled = $true
    StationDepartureWaitTimeout  = '00:10:00'
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
}
