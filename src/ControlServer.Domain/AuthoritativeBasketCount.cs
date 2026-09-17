namespace ControlServer.Domain;

/// <summary>
/// BR-013 section 2's authoritative basket count: how many baskets a sublot needs, from the MAX_BOX_COUNT
/// the SUBLOT_BOX_COUNT query returns and the approved capacity of the demand's PACKAGE.
/// </summary>
/// <remarks>
/// <para>
/// The count is <c>ceil(maxBoxCount / packageCapacity)</c>, and BR-013 makes it authoritative: no
/// default, no minimum, no operator-editable value. An input that cannot be used is therefore a failure
/// rather than a number to guess, which is why <see cref="Compute"/> answers <see langword="null"/>
/// instead of dividing by something. Which of the two facts was unusable is the caller's to name --
/// they refuse with different reason codes -- so the predicates are here too and the callers map them.
/// </para>
/// <para>
/// <b>One copy, two callers, deliberately.</b> The dispatch admission chain computes this at acceptance
/// to reserve the slots (<c>SlotCapacityCriterion</c>); the journey runtime recomputes it after the
/// operator has entered the sublot, and refuses the entry when it no longer holds
/// (<c>8005-agv-control-server#82</c>). A business rule that two sides must agree on is a rule that
/// stops being agreed on once it exists twice, so the arithmetic lives here rather than in either of
/// them -- outside the dispatch criteria directory, which batch 4 owns, and outside the runtime engine.
/// </para>
/// </remarks>
public static class AuthoritativeBasketCount
{
    /// <summary>
    /// Whether a SUBLOT_BOX_COUNT reading can be used. BR-013 section 2 fails closed on a lookup that
    /// failed, returned nothing, or returned a non-positive count.
    /// </summary>
    public static bool BoxCountIsUsable(int? maxBoxCount) => maxBoxCount is > 0;

    /// <summary>
    /// Whether a resolved PACKAGE capacity can be used: a positive number of boxes per basket. A mapping
    /// that does not exist and one that resolves to zero are both failures, and neither is a divisor.
    /// </summary>
    public static bool PackageCapacityIsUsable(int? packageCapacity) => packageCapacity is > 0;

    /// <summary>
    /// The authoritative basket count, or <see langword="null"/> when either input is unusable.
    /// </summary>
    /// <remarks>
    /// Rounded up: a partly filled basket still is one. The addition is checked because the count is
    /// taken from a peer-supplied reading, and an overflow is not a number of baskets.
    /// </remarks>
    public static int? Compute(int? maxBoxCount, int? packageCapacity)
    {
        // Written as patterns rather than through the two predicates above, so the compiler narrows the
        // arguments. Same rule, and there is nothing else for the predicates to disagree with.
        if (maxBoxCount is not > 0 || packageCapacity is not > 0)
        {
            return null;
        }

        return checked((maxBoxCount.Value + packageCapacity.Value - 1) / packageCapacity.Value);
    }
}
