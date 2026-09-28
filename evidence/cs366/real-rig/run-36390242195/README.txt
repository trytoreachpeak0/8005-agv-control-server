CI 真装置 run 36390242195（服务端 9e771922，onboard 4c2d2dc1，simulator fb5f7c59，mode=consecutive，各 1 遍）。结论 failure。
artifact real-rig-evidence 2043975 字节（非空）。
- real-onboard-compensate-then-reconnect：L2-CR-00～08 全 PASS。
- real-onboard-rebuild-stopped-cargo-handoff：L2-RH-01～11 全 PASS。
- real-onboard-cancelled-rebuild-cargo-proof：L2-RC-01～07 全 PASS（第 1 件：急停锁着期间仓空，解开后读到仓空停住，交接走到底，全程无故障货物绑定）；
  第 2 件的第二次装货在 120 秒内没等到车载端进入 WAITING_OPERATOR，脚本抛出，L2-RC-08～11 没有记下。
原因（读到的）：第二次装货又落在 1 号仓（StationOperations TargetSlotsJson=[1]），车载端当即回报装货结果 FAILED，
1 号仓 State=1（有货）、门锁着（second-load-result.json）。场景脚本在第 1 件交接之后、门关着时就把 1 号仓光幕复原成 AUTO，
而模拟器里那一仓的货物一直是「有货」（第 1 件只是把光幕固定成没挡住）——交接把货搬走这一步没有模拟到。是场景脚本的错，不是产品行为。
修法：交接之后不复原；下一次装货若落在这一仓，在车载端等操作员放货（门开着）那一刻复原，等于操作员放进新货。
