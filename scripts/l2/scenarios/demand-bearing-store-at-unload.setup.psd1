# control-server#453：需求承载 G3 的合成库生成器，不是 L2 判据，不登记进 l2.yml。
#
# 取货站离站等待缩到五秒（最小值），省掉三十秒的装置默认；别的都用合成装置的默认。
@{
    StationDepartureWaitTimeout = '00:00:05'
}
