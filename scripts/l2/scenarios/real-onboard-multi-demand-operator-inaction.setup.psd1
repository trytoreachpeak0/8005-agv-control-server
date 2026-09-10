@{
    # 真车载端 WPF + 真 slots-simulator。三幕操作员不作为全在 IO 层：光幕跳变、锁反馈时序、开锁输出
    # 复位，以及车辆自己按 IO 读数算出来的 LOCK_NOT_CLOSED。合成对端一条都证不了。
    Onboard = 'Real'

    # 种子自带的 12（N1-3_N1-7）加这三个，凑成四个不同的取货停靠。必须在这里声明，不能在运行时
    # 注入——准入策略绑定内容的那个坑，见 multi-demand-four-stops.setup.psd1。
    ExtraStations = @{
        '13' = 'N2-6'
        '14' = 'N3-4'
        '15' = 'N4-2'
    }

    # 出厂值是五分钟。压到一分钟，停靠 2 那一幕才等得到期限过期；其余三站扫码都在十秒内完成，
    # 不会被这个期限误伤。与 real-onboard-station-timeout-door-open 同一个取值。
    ServerSettings = @{
        'JourneyRuntime__sublotWaitTimeout' = '00:01:00'
    }
}
