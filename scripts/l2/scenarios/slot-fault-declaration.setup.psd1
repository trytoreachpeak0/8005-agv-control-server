### 人工判故障（REQ-0359，control-server#383）。
#
# 单车、合成车载端。一处改动环境：打开人工判故障入口。产品里它默认不挂，现场要明确打开；场景打开的是同一个开关。
@{
    SlotFaultDeclaration = $true
}
