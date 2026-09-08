# 缺陷：v2 身份切换当天暴露的三处沉默——v1 形状的报文、空切片的绿门禁、无人核对的身份

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: 票 14 的实施与双轴复审（[`14-answer.md`](https://github.com/trytoreachpeak0/8005-agv-program/blob/main/.scratch/8005-batch-2/issues/14-answer.md)）
Product at discovery: `fp/v2-impl@562544e0f8904342331cd638199954e5435e30b3`
Peers: `fp/v2-candidate@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`（协议 v2 候选，尚未打 tag）

票 14 把控制服务端的协议身份从 `protocol-v0.1.1` 切到 v2 候选。切完之后 L1
`583 passed / 0 failed / 0 skipped`——**九个身份常量全换、三条线上快照的形状全错，一条测试都没
红**。三处缺陷共用同一个成因，规格 6.3 早已写明：**两端都不做运行期 schema 校验**，两个实现
仓的依赖清单里没有任何 JSON Schema 库。这与「7 个表外 `reasonCode` 绿过八道门禁」是同一件事的
第二次、第三次、第四次发作。

三处按后果排序。

---

## D-1：服务端宣称 v2，却发着 v1 形状的报文，共 7 处

### 现象

身份切到 `protocolVersion 2` ／ `profileId AGV_FULL_PRODUCT` 之后，三条 C_TO_O 快照的 payload
与 v2 冻结的 schema 逐条冲突。schema 对这三个 payload 对象都是 `additionalProperties: false`
且属性全部 `required`，所以**多一个字段与少一个字段同样非法**。

| 消息 | 破法 | v2 schema 怎么写 |
| --- | --- | --- |
| `UpcomingStopPlanSnapshot` | payload 顶层发 `demandId` | `required: [planRevision, legs]`，`additionalProperties: false` |
| | leg 缺 `stopPurposeCategory` | 必填，**不可空**，枚举 `BUSINESS`／`WAITING_POINT`／`CHARGER` |
| | leg 缺 `demandId` | 必填，可空 |
| | leg 缺 `publicStationFunction` | 必填，可空，枚举五值 |
| | `legType` 发 `"TO_GATE"` | 枚举 `["TO_PICKUP","TO_DROPOFF"] \| null` |
| `CurrentStopWorklistSnapshot` | `stopRole` 发 `"GATE"` | 枚举 `["PICKUP","DROPOFF"]` |
| `VehicleBusinessStateSnapshot` | 完全不发 `activePurpose` | 必填，可空，枚举
`TRANSPORT`／`CHARGING`／`CLEARING_MAINTENANCE`／`IDLE_RETURN` |

`demandId` 从顶层移进 leg 不是口味问题：v2 把 `legs.maxItems` 从 2 提到 9，一条计划可以跨多个
需求，**整条快照一个需求号在语义上没有定义**。

### 为什么没被发现

没有任何一条测试断言这三条报文的字段集合。既有测试断言的是 revision 名、条数与重放字节一致
性——**重放一致性对着一份同样错误的字节也成立**。

### 处置

七处全部改对，取值逐条可解释、不需要批次 3～8 的能力：

- `stopPurposeCategory = "BUSINESS"`——本 runtime 每条腿都是把需求从取货站送到卸货站。
  `WAITING_POINT` 是 `FP-C4`（批次 5），`CHARGER` 是 `FP-C1`（批次 8），此处不存在可报的第二种。
- leg 的 `demandId` = `runtime.DemandId`，就是原先放在顶层的那一个。
- `publicStationFunction = null`——把站点绑定到公共功能是 `FP-C9b`（批次 4）。
  **从站点 id 猜一个值等于凭空发明那项能力**，可空字段的 `null` 才是如实。
- `legType`：`TO_GATE` → `TO_DROPOFF`。关卡是 v2 六类任务里的一个目的地，协议按「这条腿是什
  么」命名而不是按本 profile 唯一的那个实例命名——与 `CV-GATE-UNLOAD-ALL-EMPTY` 改名
  `CV-DESTINATION-UNLOAD-ALL-EMPTY` 是同一次改名。**服务端内部的移动目的仍叫 `TO_GATE`**，那
  是 RIoT intent 与到站目的，不是线上值，两者不得混用。
- `stopRole`：`GATE` → `DROPOFF`。
- `activePurpose = "TRANSPORT"`——跑这个 worker 的车正在运一个需求。

### 回归守卫

`ProtocolPayloadShapeArchitectureTests`：驱动**真实的** publisher 发出三条快照，捕获它写到线上
的原始字节，按 vendored schema 逐条核对「属性名集合 ＝ `required` 集合」与枚举取值。
`vendor/8005-agv-protocol/schemas/` 是协议 schema 树的整份副本（69 个文件），一个树摘要常量钉住
全部。**它不是 JSON Schema 校验器**，字符串 pattern、数值边界、format、跨字段规则都不查——
选这两条是因为破的就是这两条，而且这三个 payload 对象 `additionalProperties: false` ＋ 全部
必填，名字集合相等对它们**恰好就是**结构合规，不是它的近似。

自证跑过：把 v1 的 payload 形状原样喂进去，三处（顶层 `demandId`、leg 缺 `stopPurposeCategory`、
`legType: "TO_GATE"`）全部被报出。

---

## D-2：零测试的切片能拿到一份绿的 `CONTROL_SERVER_G2`

### 现象

`dotnet test --filter` 选不中任何测试时**退出码是 0**。`test-wire-to-gate.ps1` 把退出码直接写
成门禁结论，于是：

```json
{
  "integrationSliceId": "FP-IS-09",
  "status": "PASS",
  "testExitCode": 0,
  "vectorIds": ["CV-WORKLIST-SELECTION-ACCEPTED", "CV-WORKLIST-SELECTION-STALE-REVISION"]
}
```

`FP-IS-09` 是批次 7 的切片，服务端零实现零测试。

### 为什么现在才可能

v1 家族下不可能：`-Slice` 的正则是 `^W2G-IS-0[0-7]$`，八片都有测试。**票 14 把家族扩到 16 片，
其中 8 片排在批次 3～8**，于是同一段代码第一次有了产出假绿的输入。

### 处置

建 `-Output` 目录**之前**先 `--list-tests` 数一遍，选中 0 条就抛错并报出该片的向量，且**不创建
任何目录**。拒绝而不是写 `INCONCLUSIVE` 证据：证据目录存在就意味着跑过一次，没有测试可跑的切
片，诚实的产物是没有产物。

列表匹配锚在测试命名空间（`^\s+ControlServer\.Tests\.\S`）而不是缩进宽度——门禁级的判断不该
建立在控制台排版上。

---

## D-3：九个身份常量，没有一条测试读过

### 现象

`ProtocolCandidateIdentity` 的九个常量出现在服务端写出的**每一条报文**上，也出现在它校验对端
身份的握手里。把九个全部从 `protocol-v0.1.1` 换成 v2 候选，L1 从 569 绿到 569 绿，一条没红。

一个哈希敲错一位就会发布出去，对端拿到一份谁也配不上的期望身份，然后被「带完整期望身份地拒
绝」——而那正是规格 6.4 认为**正确**的行为，所以现场只会看到一台连不上的车，没有任何东西指向
那个错字。

### 处置

`ProtocolIdentityArchitectureTests` 五条：vendored manifest 按字节等于
`ProtocolCandidateIdentity.ManifestSha256`（**用常量核副本，不新增哈希常量**——常量让副本可信，
副本让常量可查）；其余身份字段逐个对着 manifest 的同名字段；`tag` 满足 schema 的
`minLength: 1` ＋ `^protocol-v`；`approvalStatus` 不得是 `APPROVED_RELEASE`；
`appsettings.json` 的镜像逐字段等于常量。

自证跑过：`SchemaBundleSha256` 结尾改一位，两条同时红——一条对 manifest，一条对镜像。

---

## 三条共同的教训

**「没有测试红」在这个仓库里从来不等于「没有变化」。**票 16 的向量绑定、票 09 的 reasonCode
注册表、本次的身份与报文形状，四次都是同一个形状：一份人写的清单与一份机器发出的字节之间没有
任何比对，于是清单可以静静地漂走。**每一次的解法也是同一个**：把协议仓的文件整份 vendor 进
来、按字节钉住、让测试读它而不是读一份手抄。
