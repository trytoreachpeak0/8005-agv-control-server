# G3 FP-IS-10（批次 6，control-server#164）：缺绑定的任务类型 fail-closed。真车载端 WPF + 真 slots-simulator。
#
# 不写 TaskTypeStations：用编排器默认装的出厂预置配置（control-server#159）——六类规则齐全，本图需求集与绑定只有
# WIRE_TO_GATE → 210「关卡」。STAGING_TO_WIRE 因此是「缺绑定」而不是「配错」，服务照常启动；这正是向量要证的情形。
#
# 到站期限取装置默认的 30 秒（真车载端录入实测 4.7–5.4 秒，见 real-onboard-normal-load.setup.psd1）。
@{
    Onboard = 'Real'
}
