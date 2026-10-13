### CP-0011 判定表第 4 行（control-server#539 审查 H-1）：门没锁时急停按钮按着，只记不发；修好、松开、二类入口解开后重下一次；
### 人确认门锁已检修后经有货解除入口解除。
#
# 单车、合成车载端。三处改动环境：
# - 急停重试的退避调到 20 秒，理由与 emergency-stop-single-trigger 相同：重下之后场景要在退避内把闩锁摆成锁上，
#   不让「退避到期又发了一次」混进要数的调用次数里。
# - 打开人工确认解除入口（有货那一个挂在同一个开关上，凭据单独一把）。
# - 现场人员角色名单里放一个 R-13（AGV 运维/调度管理员）：有货解除只认这个角色。
@{
    EmergencyStopRelease = $true
    FieldOperatorRoles   = @(
        @{ OperatorId = 'L2-ADMIN-13'; Roles = @('R-13') }
    )
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
}
