@{
    # 真车载端 WPF + 真 slots-simulator。
    Onboard     = 'Real'

    # 车辆安全投影经 tools/ControlServer.ClockSkewProxy 转发，observedAt 往后推这么多毫秒——
    # 从车载端 IsFresh 的角度看，等于它自己的时钟慢了这么多。
    #
    # 100 ms 落在车载端默认容差（vehicleSafety.clockSkewToleranceMs = 500）之内，所以环境起得来：
    # 这本身就是 8005-agv-onboard-hmi#1 的回归——修复之前，任何正偏差都会让会话永远进不了 Ready。
    # 场景随后在运行时把它推到容差之外再拉回来。
    ClockSkewMs = 100
}
