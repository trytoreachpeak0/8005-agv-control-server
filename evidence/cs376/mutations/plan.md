# cs#376 变异计划（先写预期，再跑）

范围过滤：FaultedCargoBindingLifecycleTests | VehicleFaultIsolationTests | StoppedRebuildExitTests | VehicleFaultRecoveryTests | OwnOrderRebuild | InTransitDoor | EmergencyReleaseVersusOwnOrderRebuild | FailedOrder
每个变异：备份→替换（断言恰好命中 1 处）→ dotnet build --no-incremental → dotnet test --no-build → 记退出码、红的用例、行号 → 从备份还原并 touch。

| # | 位置 | 变异 | 预期红（且只有这些） |
|---|---|---|---|
| M1 | JourneyRuntimeEngine.cs 卸货落定 | 删掉 StageReleaseWhenNothingLeftOnBoardAsync 调用 | AnUnloadAfterADoorFault…(ready)、(behind-the-readiness-gate)、AnUnloadAfterARebuildCancelledBeforeConfirmation…、TheFirstOfTwoUnloadsKeepsTheBindingAndTheLastReleasesIt |
| M2 | FaultedCargoBindings.StageReleaseWhenNothingLeftOnBoardAsync | 去掉「还有需求在车上就不放」的判断 | TheFirstOfTwoUnloadsKeepsTheBindingAndTheLastReleasesIt |
| M3 | VehicleFaultCoordinator.ProtectCargoAsync | 删掉先释放别的旅程绑定那一步 | ALoadedSecondTripHeldForTheDoorsResumesOnItsOwnCargo、ARealCargoOnASecondTripKeepsItsProtectionBesideAStaleBinding |
| M4 | VehicleFaultRecoveryService.ResumeAsync | 删掉读绑定前的释放 | AResumptionEntryReleasesALeftoverBindingBeforeJudgingTheCargo |
| M5 | VehicleFaultRecoveryService.DisposeOfTheJourneyAsync | 删掉判货前的释放 | AClearanceReleasesALeftoverBindingBeforeJudgingTheCargo |
| M6 | VehicleFaultRecoveryService.MayCarryAsync | 删掉读绑定前的释放 | AnEmptySecondTripStoppedByTheThirdGuardCanBeGivenUp |
| M7 | JourneyRuntimeEngine.CarriesCargoAsync | 「清除时车上有货」来源恒判有货（旧行为） | ARebuildLeftWaitingOnAnEmptySetOfSlotsGoesOnWithoutAPerson |
| M8 | JourneyRuntimeEngine.CargoEvidenceAsync | 空集照旧返回 Unproven("CARGO_SLOTS_UNKNOWN") | AnOnBoardDemandWithNoRecordedSlotsStopsTheRebuildForAPerson |
| M9 | FaultedCargoBindings.StageReleaseOfOtherJourneysCargoAsync | 去掉「所在旅程已收尾」这一半证据 | ABindingNoJourneyAccountsForIsKept；另外预计 VehicleFaultIsolationTests 里至少 ReObservingTheSameFaultDoesNotBindTheCargoTwice 会红（那个类不建旅程） |
| M10 | 同上 | 去掉「不在任何未收尾旅程里」这一半 | ABindingOfADemandStillInAnOpenJourneyIsKeptEvenIfAClosedJourneyOnceHadIt |
