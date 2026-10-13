# The real-onboard scenarios onboard-hmi#316's T3 cross-check runs on, and where each pauses (control-server#653).
# Read by L2ScreenState.psm1 when Invoke-L2Scenario.ps1 is given -ScreenState without -ScreenStatePauseAt.
#
# A point is '<criterion>=<value>' -- the first time the run's journal records that criterion flipping to exactly that
# value -- or '@dialog=<title>', a dialog found and not yet answered. Every point below is the value its wait accepts
# (so the wait returns exactly what it would have, see L2ScreenState.psm1) and a step where the product is waiting
# for the operator after the sublot was entered, so no station departure deadline is running through the pause.
# Test-L2ScreenState.ps1 checks each scenario is a real-onboard one and each criterion and title is a literal in it.
#
# Between them they put the three things T3 compares in front of the reader twice each: the status banner in its
# recovery-required state, a recovery entry offered (申请恢复, 补偿清空, before and after a restart), and a modal
# confirmation open over the window. No scenario criterion is added or changed.
@{
    Scenarios = @{
        # The HMI recovery-entry regression scenario (8005-workspace-v2 CLAUDE.md names it): restart while waiting for
        # the operator, the load settles UNKNOWN, maintenance presses 补偿清空.
        'real-onboard-compensate-then-reconnect' = @{
            PauseAt = @(
                # Banner: the journey is blocked on a load the server judged RecoveryRequired.
                'unknown-load=UNKNOWN / RecoveryRequired / Blocked'
                # Entry: 补偿清空 offered.
                'onboard-compensation-entry=True'
                # Modal: its confirmation open.
                '@dialog=补偿清空'
            )
        }
        # A recovery session opened before a restart, then compensation after it: the same entry and dialog kinds on
        # a second onboard process, so the snapshot's processStartId changes between the pauses.
        'real-onboard-restart-with-open-recovery-session' = @{
            PauseAt = @(
                'onboard-resume-entry-before-restart=True'
                '@dialog=申请恢复原操作'
                'onboard-compensation-entry-after-restart=True'
                '@dialog=补偿清空'
            )
        }
    }
}
