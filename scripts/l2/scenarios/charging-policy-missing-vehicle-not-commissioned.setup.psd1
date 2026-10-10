# 批次9-02（control-server#400）：两台车，已批准、已激活的充电策略只覆盖第二台。
#
# 主车 BROKERX-L2-0001（B）是轮次里第一个被问的车：没有本票的判据时，需求就是它的（红证据就是这样来的）。第二台
# BROKERX-L2-0002（A）在策略范围内。车载端不用再列一遍：OnboardPeers 缺席时编排器按 Fleet 逐车派生（与 three-synthetic-peers
# 一样每车一个合成对端进程）；等待点按 Fleet 默认每车一个。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    ChargingPolicy = @{ VehicleScope = @('BROKERX-L2-0002') }
}
