### 路网引擎的三种陈旧触发，各取一次 fail-closed 证据。
#
# 单车，因为这条场景与车队无关：证的是引擎自己判陈旧、判完派车就停。多加两台车只会让每轮多
# 几次被延迟的 RIoT 调用，把运行时间翻倍而不多证一件事。
#
# 三个参数是为了让「超 TTL」这一条在 L2 上真的可观测，而不是靠等：
#
#   `RuntimeStateMaxAge` 压到 5 秒，`RuntimeRefreshPeriod` 1 秒（校验器要求预算大于周期）。
#   `RefreshOnceAsync` 在一轮的**开头**取一次 now，用它给运行态盖时间戳，跑完四段读之后再用
#   **当时**的时钟判陈旧。所以只要一轮刷新自身耗时超过预算，运行态就在写下的那一刻已经过期
#   ——这正是这条兜底存在的理由：不是「没人来刷新」，而是「刷新慢到它护不住的程度」。
#   场景把假 RIoT 切到 Delay 2000ms，四段读就是 8 秒，稳过 5 秒。
#
#   `DesignStateTtl` 抬到 1 分钟，让设计态在这中间不到期也不重取：两条 TTL 同时到期时
#   `StaleReason` 只报先判的那条，测到的就不是想测的那条。
@{
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:01:00'
        RuntimeRefreshPeriod = '00:00:01'
        RuntimeStateMaxAge   = '00:00:05'
    }
}
