### control-server#335（REQ-0246）：行驶中车载端报门没锁，先按住本单、按不住再急停；门锁恢复不解除，人确认门锁已检修、
### 经有货解除入口解除（CP-0011，control-server#539），单仍停着。
#
# 单车、合成车载端。四处改动环境：
# - 急停重试的退避调到 20 秒，理由与 emergency-stop-single-trigger 相同：本装置每秒评估一次，默认 2 秒的退避会让
#   「退避内又请求了一次、正确地没发」与「退避到期、按 REQ-0248 重试」挤在一起，场景就分不清服务端做对的是哪一件。
#   20 秒比场景里要看的几个阶段加起来都长。
# - 打开人工确认解除入口（有货那一个挂在同一个开关上，凭据单独一把）。产品里默认不挂，现场要明确打开。
# - 现场人员角色名单里放一个 R-13（AGV 运维/调度管理员）：有货解除只认这个角色。
# - 打开故障人工清除入口：解除之后经故障恢复清除门锁故障（审查 S-4）。
@{
    EmergencyStopRelease = $true
    VehicleFaultRecovery = $true
    FieldOperatorRoles   = @(
        @{ OperatorId = 'L2-ADMIN-13'; Roles = @('R-13') }
    )
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
}
