@{
    # 出厂值是五分钟，一趟 L2 跑不到那么久。压到十秒，窗口两侧就都能在一次运行里穿过去——
    # 「没到期不动」和「到期就终结」是同一条判据的两半，只测一半等于没测。
    ServerSettings = @{
        'JourneyRuntime__sublotWaitTimeout' = '00:00:10'
    }
}
