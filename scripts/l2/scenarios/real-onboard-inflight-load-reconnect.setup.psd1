### 在途装货断线重连（control-server#189 复现、onboard-hmi#127 修复验证，control-server#205 转为正式场景）。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
# 在 l2.yml 的真装置清单里（CI rig=real 能跑，Runs = 1）；不在合成清单里。
#
# 站点期限放到 10 分钟：本场景要在装货进行中、装货刚结束后各观察几十秒，不让「到站期限到了」的处理
# （STATION_TIMEOUT_DOOR_NOT_CLOSED 等）混进要看的两端行为里。期待动作超时门槛保持出厂（6 分钟），全程用不到。
@{
    Onboard                     = 'Real'
    ProtocolFaultProxy          = $true
    StationDepartureWaitTimeout = '00:10:00'
}
