# 批次9-05（control-server#403）：两台车，主车 BROKERX-L2-0001（B）与第二台 BROKERX-L2-0002（A）。
#
# 场景自己经 FieldOps 导入、批准（L2_PRESET）、激活一版「强制充电线 30、余量 20、每趟估计 0」的策略，再把两车电量设成 25：落在两条线
# 之间，只有强制充电线挡得住，余量挡不住（25 − 0 ≥ 20）。默认测试策略的两条线都是 30，分不出这两条（审查 S2）。
#
# 第一段两车都是 25，所以积压行上的原因码不依赖名册次序与选车排序：不论哪辆车最后写，写的都是 MANDATORY_CHARGE_REQUIRED。第二段只把 B
# 抬到 80，需求派给 B，A 仍然哪儿也不去。
#
# 空闲返回不打开。第一版打开过，A 在场景改电量之前（服务端起来后电量还是 80 的那几轮）就合法地承诺了空闲返回
# （工作区 evidence/cs403/l2-mct-fail-1-idle-return-before-battery-set）。「低于线的车不做空闲返回」由 L1 钉住
# （IdleReturnCommitmentTests.ALowBatteryVehicleIsSentNowhereNeitherToTransportNorToAWaitingPoint、TheIdleReturnChargeLineFollowsTheChargingPolicyUpAndDown）。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
}
