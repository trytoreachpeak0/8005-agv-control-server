# 激活入口在产品里缺省不挂：它发出去的是让车换掉自己仓位 IO 绑定的那条命令。场景要证激活那条路，
# 就在这里明说打开，与现场刻意打开的是同一个开关（SlotConfigurationActivation:enabled）。
#
# 这条场景自己把已批准八仓事实入库、给车绑 IO，并断言绑上的是 8 个仓（L2-SCA-01），之后的激活也钉着那一版
# 模型与绑定，所以不要编排器的默认前置（control-server#71）：前置再绑一次，车就多出一版绑定，场景证的
# 就不再是「第一次录入之后激活」那一幕。分区归属表的导入要以已发布模型为准，这里不做入库，也就一并不导入。
@{
    SlotConfigurationActivation = $true
    SlotModelPreseed            = $false
    AreaAssignments             = $false
}
