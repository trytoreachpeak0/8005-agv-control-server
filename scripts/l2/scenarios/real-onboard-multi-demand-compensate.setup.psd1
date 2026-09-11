@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面：制造 UNKNOWN、补偿清空、装卸货全走现场驱动脚本
    # scripts/field/FieldOperator.psm1 上车的那几个函数，一次 UIA 都不用。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 补偿清空要恢复入口开着、两端凭据配齐，与 real-onboard-field-operator-compensate 同一个取值。
    RecoveryResume    = $true

    # 与 real-onboard-multi-demand-operator-inaction 同一张图：种子自带的 12 加这三个，凑成四个取货停靠。
    # 必须在这里声明，不能在运行时注入——准入策略绑定内容的那个坑，见 multi-demand-four-stops.setup.psd1。
    ExtraStations     = @{
        '13' = 'N2-6'
        '14' = 'N3-4'
        '15' = 'N4-2'
    }
}
