# 批次 5（control-server#81）：装货中期限。
#
# 派车前置用 control-server#71 起的装置默认值，只覆盖两处。
#
# 站点期限：出厂五分钟一趟 L2 跑不完；装置默认三十秒也嫌长：两段各要等一次期限走完，还要在期限之前先读一次
# 「什么都没发生」。二十秒与 station-deadline-sublot-timeout 同理，让两段都能在一次运行里穿过期限的两侧。
#
# 第二台车：两段各在一台车上扫一次码。合成对端按固定键缓存录入应答，同一台车扫第二单会把第一单的录入原样重发
# （control-server#121 在修）；两台车各扫一次，这条场景就不依赖那处修复先合入。车载端按名册逐车派生，不再列一遍。
@{
    StationDepartureWaitTimeout = '00:00:20'
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
}
