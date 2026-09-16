### G3 FP-IS-03：状态未知的装载结果在车载端重启后报为待结清、补发并对账。真车载端 WPF + 真 slots-simulator。
#
# 不开恢复入口：这条场景只证对账，不做恢复动作。车载端重启由 Context.RestartOnboard 完成，沿用同一份暂存副本与日志。
#
# 到站期限 30 秒：批次 5（control-server#79）起，同一个值也是「车到站后多久没人录入子批号，服务端就结束
# 本站、取消需求」。真装置上「服务端采信到站 → UIA 录入并提交」实测 4.7–5.4 秒
# （evidence/l2/20260913-b2close-real-onboard-normal-load-003、20260914-real-onboard-restart-with-open-recovery-session-004），
# 装置默认的 5 秒正卡在这条线上。30 秒给足余量，也远小于本场景装货提交之后那几条判据的预算。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:30'
}
