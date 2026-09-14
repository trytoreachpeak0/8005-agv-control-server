### control-server#36：开着恢复会话时重启真车载端，重连之后恢复入口还在不在。真车载端 WPF + 真 slots-simulator + 恢复入口。
### 不属于任何 G3 片：不进 scripts/run-journey-g3.ps1，也不进 scripts/g3-slice-evidence.ps1。
@{
    Onboard        = 'Real'
    RecoveryResume = $true
}
