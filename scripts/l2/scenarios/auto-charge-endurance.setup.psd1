@{
    # 自动充电出厂是关的：它要一个充电桩在活地图上的精确身份，没给就绝不能自己让车动起来。
    # 站点号与名字必须和场景脚本写进假 RIoT 地图的那一对一致——RequireFixedStation 两样都比。
    ServerSettings = @{
        'JourneyRuntime__autoChargingEnabled'         = 'true'
        'JourneyRuntime__chargerStationId'            = '充电点1'
        'JourneyRuntime__chargerStationRiotId'        = '211'
        'JourneyRuntime__chargeTriggerBatteryPercent' = '30'
        'JourneyRuntime__chargeResumeBatteryPercent'  = '80'
    }
}
