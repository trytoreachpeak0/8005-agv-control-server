全产品协议 4.0.0 正式发布（`AGV_FULL_PRODUCT`，protocolVersion 5）。取代 `protocol-v3.0.0`，是破坏性主版本：绑定 `protocol-v3.0.0` 的门禁与 L2 证据自本次发布起不再是现行证据。

| 项 | 值 |
| --- | --- |
| commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| content manifest | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| approval attestation（本 Release 的 `release-approval.json`） | `68f3516eaeb12d54cf5319e57a325362915f34ac47494712920f007cf25ad9e0` |
| schema bundle | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| vectors | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| 批准 | claude-opus-5-5 (Claude Code agent) (AI_AGENT, authorized by Zhengyu Shao) |

发布 G1 使用本 Release 附带的 attestation 运行，`status: PASS`，`failures` 为空。