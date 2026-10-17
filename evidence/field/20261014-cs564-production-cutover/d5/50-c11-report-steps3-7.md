cs#564 方案 A 第③～⑦步已完成，14:11:53 开闸（用户授权，现场有人）。证据在 production-20261014/d5/。
③ fieldops-a799c998 已发布到服务器，两端哈希一致；只读验证新库 OK（14）。
④ 导事实（16）：seed，车型 c2590b03；三车 bind-io；等待点 214/215/216（1b5e0bd9，与 10-15 相同）；充电桩名册 1ad8ad5f；充电策略 v1 已批准并激活。每区参数在 s1 下被拒（DISPATCH_ZONE_NOT_FOUND，两个区要 s3 起服后才存在），s3 起服后补导（22），fc98bae1 与 10-15 相同。
⑤ 用户选择「跳过，用过去的数据」：照 10-15 的做法，把库 C 的重提记录只改车型 id 成 c2590b03，备注写明来源 SHA 与用户 13:52 的批准（18）。三车 SLOT_CONFIGURATION_READY。
⑥ s3.runtime-on 部署（20），用户批准，无 Override。admissionPolicyVersion=6、stagingDepartureRulesEnabled=True、dispatchGeneration=100；task-type-stations 由预设自动激活，v1 ACTIVE，e642f23c。
⑦ 三车停栈，归档到 8005AGV.pre-v4-20261017，三车都是 pending=0、loadsOnBoard=0；06 两个端口都传，车载端 c9d24dfd，IO 为真模块；起栈后三车 READY；20-check 3/3 OK；区表 EB80BC22 导入，回读 aa1e990d，315 行。
开闸后（49）：意图全部 DispatchGeneration=100；agv01 …-PICKUP-100 去 99 站取货，再到 221，CONFIRMED；agv03 充电单去 211，CONFIRMED。电量：agv01 37%、agv02 24%（等桩）、agv03 12%。
需留意一条：agv02 有一条 TO_CHARGER 意图 W2G-CHARGE-62d6bd7c-20261017T060642217Z，状态 PENDING_RECONCILIATION，没有 OrderId。它是开闸前先分给 agv02、后改给 agv03 时留下的。
