# 真装置（批次9-07，control-server#405）：一台车的自动充电周期，真车载端 WPF + 真 slots-simulator。
#
# 与 G3 场景 g3-automatic-charging-cycle 同一个装置：充电桩 211「充电点1」放在假地图节点 6，路网引擎开着，单车、不打开空闲返回。
# 这一条在 l2.yml 的真装置清单里（CI rig=real 能跑），g3-* 那一条不在；两者的判据各自独立。
@{
    Onboard                     = 'Real'
    Chargers                    = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph                  = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    StationDepartureWaitTimeout = '00:00:30'
}
