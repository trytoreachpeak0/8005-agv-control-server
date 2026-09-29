#
# control-server#375：取货腿建单前那一次对账读没读到（假 RIoT 回 503），之后真车载端在场时补建。
# 到站期限沿用 real-onboard-normal-load 的 30 秒，理由见那个文件。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:30'
}
