namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// One layer of the dispatch ranking: compares two eligible candidates on one question and calls them equal on
/// everything else.
/// </summary>
/// <remarks>
/// A layer that cannot tell two candidates apart returns 0, and the next layer decides. That is how REQ-0207 drops the
/// cost layer for a comparison it cannot price without dropping the candidate, and how every later batch 7 layer (the
/// marginal cost of control-server#211, the priority band and waiting age of control-server#214) joins in: one file
/// and one line in <see cref="DispatchCandidateOrdering.Layers"/>.
/// </remarks>
public interface IDispatchCandidateComparisonLayer
{
    /// <summary>
    /// Negative when <paramref name="x"/> is taken first, positive when <paramref name="y"/> is, 0 when this layer
    /// cannot say.
    /// </summary>
    int Compare(EligibleDispatchCandidate x, EligibleDispatchCandidate y);
}

/// <summary>
/// Picks the candidate the layers put first, asking each layer in turn and the next only on a tie.
/// </summary>
/// <remarks>
/// <see cref="IDispatchCandidateRanker"/> is unchanged; this is its one implementation. On a complete tie -- two
/// candidates no layer can tell apart -- the one listed first wins, exactly as the stable <c>OrderBy(...).First()</c>
/// it replaced did. With the demand id as the last layer that cannot happen between two different demands.
/// </remarks>
public sealed class LayeredDispatchCandidateRanker(IReadOnlyList<IDispatchCandidateComparisonLayer> layers)
    : IDispatchCandidateRanker
{
    private readonly IDispatchCandidateComparisonLayer[] _layers = [.. layers];

    public EligibleDispatchCandidate SelectNext(IReadOnlyList<EligibleDispatchCandidate> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        if (eligible.Count == 0)
        {
            throw new ArgumentException("The ranker is only called with at least one candidate.", nameof(eligible));
        }

        EligibleDispatchCandidate selected = eligible[0];
        for (int index = 1; index < eligible.Count; index++)
        {
            if (Compare(eligible[index], selected) < 0)
            {
                selected = eligible[index];
            }
        }

        return selected;
    }

    private int Compare(EligibleDispatchCandidate x, EligibleDispatchCandidate y)
    {
        foreach (IDispatchCandidateComparisonLayer layer in _layers)
        {
            int order = layer.Compare(x, y);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
