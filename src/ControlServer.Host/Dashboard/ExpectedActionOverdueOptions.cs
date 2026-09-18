namespace ControlServer.Host.Dashboard;

internal sealed record ExpectedActionOverdueOptions(TimeSpan Threshold)
{
    internal const string FileName = "expected-action-overdue.settings.json";
    internal const string SectionName = "ExpectedActionOverdue";

    internal static ExpectedActionOverdueOptions Default { get; } = new(TimeSpan.Zero);

    internal static ExpectedActionOverdueOptions From(Microsoft.Extensions.Configuration.IConfiguration section) =>
        throw new NotImplementedException();
}
