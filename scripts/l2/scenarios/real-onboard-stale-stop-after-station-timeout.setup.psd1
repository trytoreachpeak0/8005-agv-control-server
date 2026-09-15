@{
    # 真车载端 + 真 slots-simulator，打开车载端 HTTP 自动化面。车上挂着哪一站、给不给「取消装货」，只有真车载端
    # 自己的投影与按钮谓词说得清——合成对端没有 _currentEntryRequest 这回事，给它跑只会绿在别的事上。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 恢复窗口不开，与 agv01 的出厂配置一致：扫码前取消只要会话 Ready 与操作员编号（CanUseStopOperator）。

    # 站点等待出厂五分钟，压到四十五秒：够车收下录入请求、自动化面看见按钮，又不至于一趟跑不完。
    # 轮询间隔用生产上的 2 秒，与现场同一节奏。
    ServerSettings    = @{
        'JourneyRuntime__pollInterval'      = '00:00:02'
        'JourneyRuntime__sublotWaitTimeout' = '00:00:45'
    }
}
