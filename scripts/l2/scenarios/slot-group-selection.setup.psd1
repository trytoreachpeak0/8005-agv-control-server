# 批次 4（control-server#73），规格 8.3 批次 4 场景①：按需求 AREA 指派的分组选仓。
#
# 覆盖默认的分区归属表（默认全是 FRONT，取仓结果与旧的全车升序取仓分不出来）：N1-3 指 REAR。场景中途再导入一版把
# N1-3 改指 FRONT，证明分组跟着库里的归属表走。入库与绑定照默认前置做：车型从库里读，脚本不写 1～4／5～8。
@{
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
