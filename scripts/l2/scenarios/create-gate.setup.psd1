# 建单前置门禁跑一趟：目录参数已批准，路网引擎开着，两个证据源都在场。
#
# 引擎必须开——门禁的分歧判定要两个源，引擎关着就只剩 RIoT 一个，那证不出「两源一致」这件事。
# getRouteCostsBy 不seed，所以每个站都答可达，与引擎自建图对同一趟路的判断一致。
@{
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
