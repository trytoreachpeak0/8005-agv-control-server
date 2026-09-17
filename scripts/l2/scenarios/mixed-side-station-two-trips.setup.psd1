# 批次 4（control-server#75），规格 8.3 批次 4 场景⑦：混挂站点，同站两侧区域号先后两趟各开各组。
#
# 取货点 12 号站整张换成 `N1-3_N2-5`：一个站点挂两个区域号，归属表把它们分到两侧。关卡 210 与 11 号站原样留着
# （Stations 是整张替换）。入库已批准八仓事实与绑车走默认前置，分组从库里读。
#
# 两台车：合成车载端一个进程只应答一次批次录入与出发前安全检查（按固定键缓存答案，第二趟收到的是第一趟的
# 批次号），单车走不了第二趟，而本票不改假对端。第一趟走完后场景在假 RIoT 上停用那台车，第二趟由另一台去同一个站点。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
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
