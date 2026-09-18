# 批次 4（control-server#75），规格 8.3 批次 4 场景⑦：混挂站点，同站两侧区域号先后两趟各开各组。
#
# 取货点 12 号站整张换成 `N1-3_N2-5`：一个站点挂两个区域号，归属表把它们分到两侧。关卡 210 与 11 号站原样留着
# （Stations 是整张替换）。入库已批准八仓事实与绑车走默认前置，分组从库里读。
#
# 一台车先后走两趟。假车载端按请求缓存答案（PR #121）之后，同一个对端进程能正确应答第二趟。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N2-5'
        '11'  = 'C15-13'
    }
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N2-5'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
