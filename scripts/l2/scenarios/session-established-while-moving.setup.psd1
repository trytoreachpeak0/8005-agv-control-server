# The synthetic peer's SafetyStateSnapshot must already say the vehicle is moving when the session
# is established -- that is the shape the 2026-09-03 field defect had, and PUT /control/v1/safety
# can only report a change to a session that already exists.
#
# departureSafe stays true on purpose. A moving vehicle can still report that nothing blocks a
# departure, and if this were false the session would never reach Ready and the run would fail at
# startup instead of at the thing it is meant to prove.
@{
    OnboardSeed = @{
        vehicleStopped = 'false'
    }
}
