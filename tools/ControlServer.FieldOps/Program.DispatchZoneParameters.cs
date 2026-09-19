using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 每区派车参数的 FieldOps 动词（control-server#216，批次7-11）：整表导入与只读查看。
/// </summary>
internal static partial class Program
{
    private const string ImportDispatchZoneParametersCommand = "import-dispatch-zone-parameters";

    private const string ReadDispatchZoneParametersCommand = "dispatch-zone-parameters";

    private static Task<int> ImportDispatchZoneParametersAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now) =>
        Task.FromResult(Usage($"{ImportDispatchZoneParametersCommand} is not implemented yet"));

    private static Task<int> ReadDispatchZoneParametersAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options) =>
        Task.FromResult(Usage($"{ReadDispatchZoneParametersCommand} is not implemented yet"));
}
