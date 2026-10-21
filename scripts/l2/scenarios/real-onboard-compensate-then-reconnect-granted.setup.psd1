### control-server#797：real-onboard-compensate-then-reconnect，服务端要恢复授权（授权开着的真装置变体）。
### 不进 l2.yml（真装置不上 CI）；第一次真跑是 cs#797 与 onboard-hmi#391 的真装置联跑。
#
# 与原场景相同的三项之外两项：
# - 打开故障人工恢复入口：授权入口挂在同一个开关上，用同一把凭据；
# - 打开「要授权」：产品缺省关，v2 实例定义在恢复入口开着时随之打开；场景打开的是同一个开关。
@{
    Onboard               = 'Real'
    RecoveryResume        = $true
    ProtocolFaultProxy    = $true
    VehicleFaultRecovery  = $true
    RecoveryGrantRequired = $true
}
