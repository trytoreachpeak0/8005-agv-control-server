using ControlServer.Domain;
using ControlServer.Host.Composition;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 仓位配置就绪门禁的档位开关，与 W1 现场窗口的顺序纪律（规格 3.5、8.4）。
/// </summary>
public sealed class SlotConfigurationGateModeTests
{
    [Fact]
    public void TheGateIsOffUntilSomebodyTurnsItOn()
    {
        // 缺省必须是关的。一个默认打开的门禁会在第一台车核对完之前就把三台现有车一起挡住，而那正是
        // 3.5 明确排除的排法——W1 先核对、后启用，零停产靠的就是这个次序。
        Assert.Equal(
            SlotConfigurationGateMode.Off,
            GovernanceModule.ResolveGateMode(new ConfigurationBuilder().Build()));

        Assert.Equal(
            SlotConfigurationGateMode.Enforcing,
            GovernanceModule.ResolveGateMode(Configuration("Enforcing")));

        // 大小写不敏感：现场敲的是一行配置，不是一个标识符。
        Assert.Equal(SlotConfigurationGateMode.Enforcing, GovernanceModule.ResolveGateMode(Configuration("enforcing")));
        Assert.Equal(SlotConfigurationGateMode.Off, GovernanceModule.ResolveGateMode(Configuration("off")));
    }

    [Fact]
    public void AMisspelledModeFailsAtStartupInsteadOfSilentlyMeaningOff()
    {
        // 拼错的档位名如果被静默降级成 Off，现场会以为门禁开着——那是最坏的一种「配置生效了」。
        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => GovernanceModule.ResolveGateMode(Configuration("Enforceing")));
        Assert.Contains(GovernanceModule.GateModeKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SlotConfigurationGateMode.Enforcing), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheGateOnIsNotTheSameThingAsTheGateStoppingAVehicle()
    {
        // 这条测试记录的是一个**边界**，不是一个能力：当前 src/ 里没有任何生产调用点读
        // IsSlotConfigurationConfirmedAsync——把 readiness 接进投运判定属于投运流程，不属于批次 3 的
        // 现场票。W1 的证据因此只能说「readiness 是怎么取得的」，不能说「门禁在拦车」。
        //
        // 这条会在接线真的落地那天变红，那时把它改掉，并同时改掉 W1 手册与 SUMMARY 模板里的那段话。
        string root = FindRepositoryRoot();
        string[] callSites =
        [
            .. Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.EndsWith("SlotConfigurationReadinessGate.cs", StringComparison.Ordinal))
                .Where(file => File.ReadAllText(file)
                    .Contains("IsSlotConfigurationConfirmedAsync", StringComparison.Ordinal))
        ];
        Assert.Empty(callSites);
    }

    private static IConfiguration Configuration(string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [GovernanceModule.GateModeKey] = value })
            .Build();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }
}
