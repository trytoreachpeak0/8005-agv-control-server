### G3 FP-IS-03：状态未知的装载结果在车载端重启后报为待结清、补发并对账。真车载端 WPF + 真 slots-simulator。
#
# 不开恢复入口：这条场景只证对账，不做恢复动作。车载端重启由 Context.RestartOnboard 完成，沿用同一份暂存副本与日志。
@{
    Onboard = 'Real'
}
