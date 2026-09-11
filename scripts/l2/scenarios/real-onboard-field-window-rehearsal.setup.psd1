@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面：现场窗口一（无人）的驱动脚本与证据采集器
    # 一起在这里彩排，驱动走的正是上车的那几个函数。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 现场窗口里恢复入口是开着的（SC1-W-02），场景 A 的「HMI 上没有出现恢复入口」也只有在入口
    # 开着时才问得出东西——关着时按钮本来就不会显示。
    RecoveryResume    = $true

    # 与 real-onboard-multi-demand-operator-inaction 同一张图：种子自带的 12 加这三个，凑成四个取货停靠。
    ExtraStations     = @{
        '13' = 'N2-6'
        '14' = 'N3-4'
        '15' = 'N4-2'
    }

    # 出厂五分钟压到一分钟，停靠 2 才等得到期限；停靠 1 的两轮空关在十几秒内做完，不会被它误伤。
    ServerSettings    = @{
        'JourneyRuntime__sublotWaitTimeout' = '00:01:00'
    }
}
