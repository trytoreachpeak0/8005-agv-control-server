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
#
# 批次9-06（control-server#404）起，低于强制充电线的空闲车会被分配去充电；名册里没有它可用的桩时则进人工充电等待——那时挡住搬运的是
# 等待，积压行上的原因码不再是 MANDATORY_CHARGE_REQUIRED，抬高电量也不恢复资格。这条场景要证的仍是强制充电线本身，所以让两台车停在
# 「需要充电、名册里有桩、但此刻分不到」这一格：场景导入一版登记站 211 的名册，而路网引擎没开——充电分配算不出桩可不可达，
# 一台也不分、也不置等待，车留在队里。Chargers 只把站 211 放到假地图上（不给 Node）。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1' }
    )
}
