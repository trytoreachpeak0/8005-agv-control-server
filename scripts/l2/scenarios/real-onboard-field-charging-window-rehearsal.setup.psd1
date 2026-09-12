@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面：现场窗口二的充电短窗口
    # （Invoke-FullLoopFieldDrive.ps1 -ChargingOnly）在这里彩排，动作与采集器都是上车的那一份。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 第二趟两条需求用 N1-3 与 N2-6，再加充电桩 211。211 的名字必须与下面 chargerStationId 一致，
    # 否则服务端从 8005-agv-program#53 起桩解析不到就拒绝一切接单（real-onboard-field-window2-rehearsal-004）。
    ExtraStations     = @{
        '13'  = 'N2-6'
        '211' = '充电点1'
    }

    # 充电四项与生产 appsettings.json 同值写明；轮询间隔用生产上的 2 秒（8005-agv-program#52 的教训）。
    # 站点期限不压：这一窗没有场景 T。
    ServerSettings    = @{
        'JourneyRuntime__pollInterval'                = '00:00:02'
        'JourneyRuntime__autoChargingEnabled'         = 'true'
        'JourneyRuntime__chargerStationId'            = '充电点1'
        'JourneyRuntime__chargerStationRiotId'        = '211'
        'JourneyRuntime__chargeTriggerBatteryPercent' = '30'
        'JourneyRuntime__chargeResumeBatteryPercent'  = '80'
    }
}
