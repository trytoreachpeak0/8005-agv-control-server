@{
    # 真车载端 WPF + 真 slots-simulator，打开车载端 HTTP 自动化面：扫码、装货、补偿清空全走现场驱动脚本
    # scripts/field/FieldOperator.psm1 上车的那几个函数。车载端那一半——命令到达时目标仓已有货就不开门、
    # 照实上报并把这次 attempt 记进日志——只有真车载端证得了，合成对端按策略应答，根本没有 IO。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 两幕都要补偿清空：恢复入口开着、两端凭据配齐，与 real-onboard-field-operator-compensate 同一个取值。
    RecoveryResume    = $true

    # 出厂五分钟，压到一分钟：第二幕要等本站期限过去，车才会把「第三仓一直空关」结算成 FAILED。
    # 其余几站扫码都在十秒内完成，不会被它误伤。与 real-onboard-multi-demand-operator-inaction 同一个取值。
    ServerSettings    = @{
        'JourneyRuntime__sublotWaitTimeout' = '00:01:00'
    }
}
