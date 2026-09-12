@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面：现场窗口二（无人）的动作与证据采集器
    # 一起在这里彩排，走的正是上车的那几个函数。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 恢复窗口不开。窗口二唯一接近恢复的动作是扫码前「取消装货」，它只要会话 Ready 与操作员编号
    # （CanUseStopOperator），不要恢复凭据——现场同样不开窗，这里就不该靠开窗绿。

    # 与 real-onboard-field-window-rehearsal 同一张图凑出四个取货停靠，再加充电桩 211。启动前就要在图上：
    # 准入策略把版本绑在首次看到的站点集合上（Invoke-L2Scenario.ps1 的 ExtraStations 注释）。桩的名字不是
    # 区号格式，那个集合原样不动。
    ExtraStations     = @{
        '13'  = 'N2-6'
        '14'  = 'N3-4'
        '15'  = 'N4-2'
        '211' = '充电准备点1'
    }

    # 站点期限出厂五分钟压到一分钟，场景 T 才等得起。充电四项与生产 appsettings.json 同值写明，
    # 而不是靠默认：桩的编号与名字必须和上面的图对上，RequireFixedStation 两样都要精确匹配。
    # 轮询间隔用生产上的 2 秒，不用 L2 默认的 1 秒。车给出车前安全检查的回答只有 2 秒有效期：-002 在 1 秒间隔下
    # 全绿，现场第一次跑就卡在停靠 2（8005-agv-program#52）——结束本站的那一轮没有当场判回答，下一轮已经过期。
    ServerSettings    = @{
        'JourneyRuntime__pollInterval'                = '00:00:02'
        'JourneyRuntime__sublotWaitTimeout'           = '00:01:00'
        'JourneyRuntime__autoChargingEnabled'         = 'true'
        'JourneyRuntime__chargerStationId'            = '充电准备点1'
        'JourneyRuntime__chargerStationRiotId'        = '211'
        'JourneyRuntime__chargeTriggerBatteryPercent' = '20'
        'JourneyRuntime__chargeResumeBatteryPercent'  = '80'
    }
}
