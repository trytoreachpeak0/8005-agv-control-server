# 两台车、只登记一个等待点：规格 5.4，投运车辆数大于 1 而等待点少于车辆数时服务端拒绝以该配置启动（REQ-0289，control-server#388）。
#
# Fleet 列的是主车之外的车，所以这是两台车。WaitingPoints = 1 让编排器只登记站 214「等待点1」——照样走 --migrate-only 建库、
# FieldOps import-waiting-points 正式导入，只是比默认（每车一个）少一个。
# ExpectServerStartupRefusal 让编排器改为等服务端进程退出、收日志，不起对端，再交给场景判定。
@{
    Fleet                      = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    WaitingPoints              = 1
    ExpectServerStartupRefusal = 'WAITING_POINTS_FEWER_THAN_VEHICLES'
}
