# 批次 4（control-server#68）：分区归属表整表导入的五类配置错误各自整份拒绝。
#
# 场景断言导入之前库里一版分区归属表都没有（L2-AAI-03），再逐份导入、数版本号，所以编排器默认前置里那一次
# 导入不能做（control-server#71）。入库与绑定照做：场景自己的 seed-approved-facts 是幂等的，拿回的就是同一版。
@{
    AreaAssignments = $false
}
