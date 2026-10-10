# 作废：相对路径的 -Output，schema 一致性结果缺失（不是门禁结论）

control-server#393 出口的第一遍 `CONTROL_SERVER_G2`，2026-10-09 夜在本机跑，实现提交 `f63732b87b3e63deaba84e77b37f3ff6fb9c66f1`（第一次移 G3 绑定的那个提交）。

15 片的 `gate-result.json` 都写着 `status PASS`，但**每一片的 `schemaConformance` 都是空值**（逐份读到的）。原因是调用方式错了：传的是相对路径的 `-Output`。G2 入口在测试程序集那一侧生成 schema 一致性报告，相对路径被解析到了别的目录，报告没有回到这里，门禁结果里于是没有这一项。

这一项是 G2 的一部分：它把测试发出的每一行协议消息拿去对协议 JSON Schema。没有它，这 15 个 PASS 只说明测试通过，不说明发出的消息合乎 schema，所以**这一遍按作废处理**（调度裁定）。门禁在 schema 没有校验时仍然判 PASS，这本身是一个缺陷，由 control-server#559 承接。

改用绝对路径重跑，15 片全部 PASS，每片 `schemaConformance` 都有结果、违规 0。正式证据在同级目录 `20261010-protocol-v3.0.0-b6be67ac/`，出口报告第三节「服务端 G2」。

本目录原样保留，不改任何文件。
