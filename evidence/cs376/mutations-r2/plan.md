# cs#376 变异计划，第二轮（审查之后，先写预期，再跑）

范围过滤不变（8 个故障与重建相关的类，`OwnOrderRebuild` 前缀也覆盖 `OwnOrderRebuildCargoEvidenceRequestTests`）。每个变异：备份→替换（断言恰好命中 1 处）→ `dotnet build --no-incremental` → `dotnet test --no-build` → 记退出码、红的用例、行号、替换前后文本 → 从备份还原并 touch。

| # | 位置 | 变异 | 预期红（且只有这些） |
|---|---|---|---|
| M1 | JourneyRuntimeEngine.cs 卸货落定 | 删掉按旅程收口的调用 | AnUnloadAfterADoorFault…(2 格)、AnUnloadAfterARebuildCancelledBeforeConfirmation…、TheFirstOfTwoUnloads…、WhenTheAnchorUnloadsLast… |
| M2 | FaultedCargoBindings.StageReleaseWhenNothingLeftOnBoardAsync | 「还有需求在车上就不放」恒假 | TheFirstOfTwoUnloads…、WhenTheAnchorUnloadsLast… |
| M3 | VehicleFaultCoordinator.ProtectCargoAsync | 删掉先释放别的旅程的绑定 | ALoadedSecondTrip…、ARealCargoOnASecondTrip… |
| M4 | VehicleFaultRecoveryService.ResumeAsync | 删掉放行时的释放 | AResumptionEntryReleases… |
| M5 | VehicleFaultRecoveryService.DisposeOfTheJourneyAsync | 删掉判货前的释放 | AClearanceReleases… |
| M6 | StoppedRebuild.MayCarryAsync | 别的旅程的绑定也算有货 | AnEmptySecondTripStoppedByTheThirdGuardCanBeGivenUp |
| M7 | StoppedRebuild.TerminateStoppedAsync | 删掉放行时的释放 | AnEmptySecondTripStoppedByTheThirdGuardCanBeGivenUp（断言释放原因） |
| M8 | JourneyRuntimeEngine.CarriesCargoAsync | 「清除时车上有货」来源恒判有货 | ARebuildLeftWaitingOnAnEmptySetOfSlotsGoesOnWithoutAPerson |
| M9 | JourneyRuntimeEngine.CargoEvidenceAsync | 空集照旧返回 Unproven("CARGO_SLOTS_UNKNOWN") | AnOnBoardDemandWithNoRecordedSlotsStopsTheRebuildForAPerson |
| M10 | FaultedCargoBindings.OtherJourneysCargoAsync | 去掉「所在旅程已收尾」这一半证据 | ABindingNoJourneyAccountsForIsKept、VehicleFaultIsolationTests.ReObservingTheSameFaultDoesNotBindTheCargoTwice |
| M11 | 同上 | 去掉「不在任何未收尾旅程里」这一半 | ABindingOfADemandStillInAnOpenJourneyIsKeptEvenIfAClosedJourneyOnceHadIt |
| M12 | OnboardRecoveryCoordinator.SettleHandedOffCargoAsync | 删掉按旅程收口的调用 | AHandoffReleasesABindingNamingAnEndedDemandOfTheOpenJourney（FAULT_CARGO_HANDOFF、FORCED_MECHANICAL_RECOVERY 两格） |
| M13 | OwnOrderRebuilds.ClaimCargoEvidenceRequestAsync | 「清除时车上有货」来源不看旅程，恒要快照 | ARebuildLeftWaitingOnAnEmptySetOfSlotsGoesOnWithoutAPerson、OwnOrderRebuildCargoEvidenceRequestTests.Req0362NothingIsAskedWhenNoCargoRebuildWaitsForEvidence(cleared-loaded-nothing-loaded) |
| M14 | VehicleFaultRecoveryService.ResumeAsync | 判拒时不把别的旅程的绑定当作不存在 | AResumptionEntryReleases… |
| M15 | 同上 | 释放挪回判拒之前（不看有没有拒绝） | ARefusedRequestReleasesNothing(resume-not-remedied) |
| M16 | StoppedRebuild.MayCarryAsync | 判货时就释放（旧写法） | ARefusedRequestReleasesNothing(give-up-under-latch) |
