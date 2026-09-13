@{
    # 与 real-onboard-field-continuous-rehearsal 同一套装置：真车载端 + 真 slots-simulator + 车载端 HTTP 自动化面。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 同一张图：四个取货站加充电桩 211。
    ExtraStations     = @{
        '13'  = 'N2-6'
        '14'  = 'N3-4'
        '15'  = 'N4-2'
        '211' = '充电点1'
    }

    # 站点期限出厂五分钟压到一分钟，「无人扫码」一幕才等得起；判据只看它是不是被期限终结，不看等了多久。
    # 轮询间隔与充电四项与生产 appsettings.json 同值。
    ServerSettings    = @{
        'JourneyRuntime__pollInterval'                = '00:00:02'
        'JourneyRuntime__sublotWaitTimeout'           = '00:01:00'
        'JourneyRuntime__autoChargingEnabled'         = 'true'
        'JourneyRuntime__chargerStationId'            = '充电点1'
        'JourneyRuntime__chargerStationRiotId'        = '211'
        'JourneyRuntime__chargeTriggerBatteryPercent' = '30'
        'JourneyRuntime__chargeResumeBatteryPercent'  = '80'
    }
}
