# 已导入未批准的充电策略版本不投运（批次 9 A 档「策略版本没批不投运」补证，2026-10-11 17:06～17:09）

V2 并行实例（`0c396ba7`，newdb4），agv02 停在等待点 216，电量 96%，闸门关着。不动车。用户经调度定「今晚先做那两项」。

## 结论

**通过。**未批准的版本导入后不生效，试激活被拒；生效的仍是已批准的 v1。

| 判据 | 结果 | 出处 |
| --- | --- | --- |
| 导入新版本 v3（强制线 98、完成线 99，不批准） | 17:06:42 `import-charging-policy` OK，v3 批准数 0 | `02-import-v3-unapproved.txt` |
| 未批准版本不生效 | 17:08:11 与 17:09:36 两次读回：`activeVersion=1`（30／80），车 `CHARGING_POLICY_EFFECTIVE` 按 v1，无打开的充电周期，充电周期表仍只有晚段 F 那一条 | `03`、`05`、`07` |
| 未批准版本不能激活 | 17:09:34 `activate-charging-policy --version 3`：`REJECTED`，`CHARGING_POLICY_VERSION_NOT_APPROVED`（`Charging policy version 3 has no approval; only an approved version can be activated (REQ-0282).`） | `07-activate-v3-refused-and-check.txt` |

## 为什么判据落在「激活被拒」上，而不是「派车日志里没有充电」

派车与充电选策略只看已激活的版本（`ChargingPolicyGovernance.cs:619-653` `ResolveForNewDecisionAsync` → `ReadEffectiveForVehicleAsync`），激活要求至少一条批准（`:392-401`）。导入后的日志里没有任何充电判断，但这不能单独当证据：关闸重启（16:53:27）之后这台实例一条派车判断都没写过（`06-rounds-after-gate-close.txt`），派车判断在没有变化时不写日志，对照不干净。所以把判据放在治理入口：未批准版本走不到激活，也就到不了派车读取的那一层。

## 未批准版本的去向

FieldOps 没有撤回策略版本的命令，v3 **留存**为未批准状态（照实记）。如果用户授权「充满离桩接活」，v3 的内容正好是那一段要的临时策略（电量加 2、完成线 99），会走受治理路径批准并激活后使用，用完换回 v1。
