@{
    # 三个额外的取货站点，加上种子自带的 12（N1-3_N1-7），凑成四个不同的取货停靠。
    #
    # **必须在这里声明，不能由场景在运行时注入。**准入策略把版本号绑定在它第一次看到的内容上，
    # 服务端起来之后再改地图，每一轮 tick 都会抛
    # `Admission policy version is already bound to different content or deployment identity`，
    # 而那个抛出发生在需求循环之前——backlog 一行不写，任何原因码都不出现。表现是引擎永远在轮询、
    # 一条都不受理，日志干干净净。
    ExtraStations = @{
        '13' = 'N2-6'
        '14' = 'N3-4'
        '15' = 'N4-2'
    }
}
