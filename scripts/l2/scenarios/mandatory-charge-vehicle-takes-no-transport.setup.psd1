# 批次9-05（control-server#403）：两台车，主车 BROKERX-L2-0001（A）电量由场景设到测试策略的强制充电线（30）以下、停在取货站，第二台
# BROKERX-L2-0002（B）是 80、停在关卡。A 离需求更近：没有电量那一段，需求就是 A 的（红证据就是这样来的）。
#
# 空闲返回不打开。第一版打开过，结果 A 在场景把电量改成 25 之前（编排器起来后、电量还是 80 的那几轮里）就合法地承诺了空闲返回
# （工作区 evidence/cs403/l2-mct-fail-1-idle-return-before-battery-set）：场景改不了服务端起来之后头几轮的电量。「低于线的车不做空闲返回」由 L1 钉住
# （IdleReturnCommitmentTests.ALowBatteryVehicleIsSentNowhereNeitherToTransportNorToAWaitingPoint、TheIdleReturnChargeLineFollowsTheChargingPolicyUpAndDown）。
#
# 路网引擎开着：边际成本按车所在的路网节点算，停在取货站的 A 才真的「更近」。合成 RIoT 的 RouteCost 对每辆车都答同一个数，不开路网时
# 距离层分不出两车，电量这一末级裁决让 80% 的 B 本来就排在 25% 的 A 前面——那样红证据证不出东西（工作区 evidence/cs403/ 里第一、二次取红）。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
