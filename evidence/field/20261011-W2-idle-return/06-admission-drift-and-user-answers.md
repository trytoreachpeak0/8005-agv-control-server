# 准入策略漂移（2116）与 E 段暂停（2026-10-11 13:00）

## 读代码与日志的结论（da6e40e2）

- 2116 首次 12:47:22（UTC 04:47:22），到 12:55:30 共 244 次。增：T4-1…T4-8、T5-1…T5-8；减：T04-01…T04-08、T05-01…T05-08。准入版本 3，26 号图。C 段（12:40 结束）不受影响。
- `JourneyRuntimeEngine.cs:594-611`：绑定失败（BusinessIdentityConflictException）→ `admissionPolicyDrifted=true` 并记 2116；之后照常落实空闲返回、推进在途旅程。
- 新需求过准入链时被 `AdmissionPolicyDriftCriterion`（Order 75）以 `ADMISSION_POLICY_DRIFT` 拒绝（缺陷记录 docs/defects/20260915-admission-policy-drift-halts-runtime.md 第 3 条）。所以现在注入 E 段需求不会被受理。
- 恢复（同缺陷记录「恢复接单」）：确认新集合 → `admissionPolicyVersion` 加 1 → 重启。

## 空闲返回建单（另一回事，与漂移无关）

12:50:07 唯一一次建单 RIoT 回 HTTP 200 + 00002（SdkFailure / RIOT_API_FAILURE）→ CreateResponseUnknown；此后每 2 秒对账 AbsentAtObservation，首 12:50:09、到 12:53:27 共 103 条审计事件（含建单 3 条），不重试不放弃（`05-idle-create-result-unknown.txt`）。

## 用户答复（本会话窗口）

- T 站改名：「是长期改名」
- 地图状态：「已经改完并发布」
- E 段：「先等，不动」
