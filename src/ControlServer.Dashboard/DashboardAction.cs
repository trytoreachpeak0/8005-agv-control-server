using System.Reflection;

namespace ControlServer.Dashboard;

/// <summary>How a field shows on an action's confirmation page.</summary>
public enum DashboardActionFieldKind
{
    Hidden,
    Text,
    TextArea
}

/// <summary>One field of an action's confirmation page.</summary>
public sealed record DashboardActionField(string Name, string Label, DashboardActionFieldKind Kind, bool Required);

/// <summary>A dashboard write action (control-server#162).</summary>
public interface IDashboardAction
{
    string ActionId { get; }

    string Title { get; }

    string TargetPath { get; }

    IReadOnlyList<DashboardActionField> Fields { get; }

    object BuildRequest(IReadOnlyDictionary<string, string> form);
}

/// <summary>The reflection-discovered actions.</summary>
public sealed class DashboardActionCatalog
{
    public DashboardActionCatalog(IEnumerable<IDashboardAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Actions = [.. actions];
    }

    public static DashboardActionCatalog Discovered { get; } = new([]);

    public IReadOnlyList<IDashboardAction> Actions { get; }
}
