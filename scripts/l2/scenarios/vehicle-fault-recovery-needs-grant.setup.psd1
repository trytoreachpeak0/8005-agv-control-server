### 故障人工恢复入口要按车限时授权（control-server#797，ADR-cross-0066）。
#
# 单车、合成车载端。两处改动环境：
# - 打开故障人工恢复入口（授权入口挂在同一个开关上，用同一把凭据）。
# - 打开「要授权」：产品缺省关，v2 实例定义在恢复入口开着时随之打开；场景打开的是同一个开关。
@{
    VehicleFaultRecovery = $true
    RecoveryGrantRequired = $true
}
