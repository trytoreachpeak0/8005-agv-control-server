### control-server#335 开工查证：车在路上时，服务端手里有没有新鲜的门锁事实；断线重连后多久恢复。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
#
# 到站期限 5 分钟：关卡段里要依次做断线重连、锁反馈覆盖、Modbus 不回应，每段都要等服务端看到结果，
# 编排器默认的 30 秒会先把取货站结掉。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:05:00'

    # 断线重连那一段用 POST /control/v1/disconnect：不丢任何行，只断开，车自己重连。
    ProtocolFaultProxy          = $true
}
