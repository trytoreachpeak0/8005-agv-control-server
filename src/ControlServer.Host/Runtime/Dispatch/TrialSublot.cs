using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 试验机批号规则（REQ-0368，CP-0023，control-server#828）。
/// </summary>
public sealed record TrialSublotRule(int Position, string Characters)
{
    public static TrialSublotRule Off { get; } = new(0, "");

    public static TrialSublotRule From(JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Off;
    }

    public bool Matches(string? sublot)
    {
        _ = sublot;
        return false;
    }
}
