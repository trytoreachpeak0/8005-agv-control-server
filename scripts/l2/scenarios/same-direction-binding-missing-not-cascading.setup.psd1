# 批次10-02（control-server#546），规格 8.3 批次 10「同向四类缺绑定不投运」。
#
# 预置配置不写：编排器默认的那一份只绑 WIRE_TO_GATE → 210「关卡」，本图需求集合也只有它，同向四类一个都没绑。
# Stations 整张替换只为多出 AREA：四类与 WIRE_TO_GATE 五条需求的 AREA 互不相同（REQ-0187 的跨任务类型唯一性不先挡掉
# 别的，见 task-type-binding-missing-not-cascading），而默认站表只给出三个。13 号站挂 N2-1、N2-2 两个。默认归属表按
# 替换后的站名推 AREA，全部归本分区、FRONT。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '13'  = 'N2-1_N2-2'
    }
}
