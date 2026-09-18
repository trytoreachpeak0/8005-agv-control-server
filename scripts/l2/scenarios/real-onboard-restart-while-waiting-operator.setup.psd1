### control-server#88（program#61 ②，ADR-cross-0058 决策 2 的正面）：开锁等人时车载端进程没了，重启后按实时 IO 补交结果。
### 真车载端 WPF + 真 slots-simulator。不属于任何 G3 片，不进 l2.yml（真装置不上 CI）。
#
# 出厂配置：不开 RecoveryResume。这条路径的要点正是「不进恢复」，所以恢复入口开着与否不该影响它；关着跑，
# 才证得出中断结算本身不依赖那个开关。
# 到站期限用编排器默认的 30 秒：计时从到站起、到录入为止（control-server#79），重启发生在录入之后。
@{
    Onboard = 'Real'
}
