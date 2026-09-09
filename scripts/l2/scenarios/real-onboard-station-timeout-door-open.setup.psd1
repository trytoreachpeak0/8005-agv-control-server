@{
    # 真车载端 WPF + 真 slots-simulator。本条要的是「车辆真的在上报 LOCK_NOT_CLOSED」，
    # 而那份安全投影由车载端自己按 IO 读数算出来（WireToGateSafetyEvaluator）——合成对端
    # 没有这段逻辑，给它设个种子只是让服务端收到一个字符串，证不出决策 4。
    Onboard       = 'Real'

    # 出厂值是五分钟。压到一分钟：既要留出注入光幕/锁反馈故障并等它经安全投影传到服务端的
    # 时间（车载端 1 秒一轮），又不能长到一趟 L2 跑不完。
    ServerSettings = @{
        'JourneyRuntime__sublotWaitTimeout' = '00:01:00'
    }
}
