@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面：连续驱动 Invoke-FieldContinuousDrive 走的正是上车的那几个函数。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 恢复窗口不开：连续驱动只做普通装卸，不碰恢复入口，现场同样不开窗。

    # 与 real-onboard-field-window2-rehearsal 同一张图：四个取货站加充电桩 211，启动前就要在图上（准入策略把版本绑在
    # 首次看到的站点集合上）。211 的名字必须与 chargerStationId 一致。
    ExtraStations     = @{
        '13'  = 'N2-6'
        '14'  = 'N3-4'
        '15'  = 'N4-2'
        '211' = '充电点1'
    }

    # 站点期限用出厂值：连续驱动不演「没人扫码」，不需要压短。轮询间隔与充电四项与生产 appsettings.json 同值。
    ServerSettings    = @{
        'JourneyRuntime__pollInterval'                = '00:00:02'
        'JourneyRuntime__autoChargingEnabled'         = 'true'
        'JourneyRuntime__chargerStationId'            = '充电点1'
        'JourneyRuntime__chargerStationRiotId'        = '211'
        'JourneyRuntime__chargeTriggerBatteryPercent' = '30'
        'JourneyRuntime__chargeResumeBatteryPercent'  = '80'
    }
}
