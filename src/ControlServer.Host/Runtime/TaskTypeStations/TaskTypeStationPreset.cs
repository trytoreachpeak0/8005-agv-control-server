using System.Globalization;
using ControlServer.Application;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>
/// 任务类型规则与按图绑定的受控预置配置，放自己的文件 <see cref="FileName"/>，不进 <c>appsettings.json</c>（control-server#159）。
/// </summary>
/// <remarks>
/// <para>
/// 读法照 <c>BlockedJourneyEscalationOptions</c>：默认读宿主目录里的文件，环境变量（例如 <c>TaskTypeStations__mapId</c>）照样盖在文件之上。
/// 另外可以用主配置的 <see cref="SettingsFileKey"/>（环境变量 <c>TaskTypeStations__settingsFile</c>）整份换一个文件，L2 编排器就是这么
/// 给每次运行装它自己的一份；点名的文件不存在时拒绝启动，而不是悄悄当成没配。
/// </para>
/// <para>
/// 文件里没有 <see cref="SectionName"/> 这一节时返回 <c>null</c>：不装载、不拒绝启动、不动生效指针。已有生效版本的图照旧用它
/// （规格 21.2 第 4 条）；从没有生效版本的图，批次6-04 让它所有任务类型都「未启用」。
/// 字段类型错（例如 <c>stationRiotId</c> 不是整数）时抛 <see cref="InvalidDataException"/> 并点名字段；内容是否合规由
/// <see cref="TaskTypeStationConfigurationValidator"/> 判，这里只负责读出来。
/// </para>
/// </remarks>
public static class TaskTypeStationPreset
{
    public const string FileName = "task-type-stations.settings.json";
    public const string SectionName = "TaskTypeStations";

    /// <summary>主配置里整份换文件的键；相对路径按宿主目录解析。</summary>
    public const string SettingsFileKey = "TaskTypeStations:settingsFile";

    public static TaskTypeStationPresetFile? Load(string baseDirectory, string? settingsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        bool named = !string.IsNullOrWhiteSpace(settingsFile);
        string path = named
            ? Path.GetFullPath(settingsFile!, baseDirectory)
            : Path.Combine(baseDirectory, FileName);
        if (!File.Exists(path))
        {
            return named
                ? throw new FileNotFoundException(
                    $"{SettingsFileKey} names {path}, which does not exist; refusing to start without the preset it names.",
                    path)
                : null;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(path)!)
            .AddJsonFile(Path.GetFileName(path), optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        IConfigurationSection section = configuration.GetSection(SectionName);
        // The file-override key itself arrives here through the environment; it is not preset content.
        if (!section.GetChildren().Any(child => !string.Equals(child.Key, "settingsFile", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return new TaskTypeStationPresetFile(path, Parse(section));
    }

    private static TaskTypeStationConfiguration Parse(IConfigurationSection section)
    {
        TaskTypeStationRule[] rules =
        [
            .. section.GetSection("rules").GetChildren().Select(rule =>
                new TaskTypeStationRule(rule["taskType"] ?? string.Empty, rule["fixedEnd"] ?? string.Empty))
        ];
        string[] required =
        [
            .. section.GetSection("requiredTaskTypes").GetChildren().Select(taskType => taskType.Value ?? string.Empty)
        ];
        TaskTypeStationBinding[] bindings =
        [
            .. section.GetSection("bindings").GetChildren().Select(binding => new TaskTypeStationBinding(
                binding["taskType"] ?? string.Empty,
                ReadInt(binding.GetSection("stationRiotId")),
                binding["stationName"] ?? string.Empty,
                binding["siteVerificationRef"] ?? string.Empty))
        ];
        return new TaskTypeStationConfiguration(
            rules,
            new TaskTypeStationMapConfiguration(ReadInt(section.GetSection("mapId")), required, bindings));
    }

    /// <summary>A missing number reads as 0, which the validator refuses as an invalid identity; a non-number is refused here.</summary>
    private static int ReadInt(IConfigurationSection value)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
        {
            return 0;
        }
        return int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidDataException($"{value.Path} is '{value.Value}'; it must be an integer.");
    }
}

/// <summary>读到的预置配置与它来自哪个文件。</summary>
public sealed record TaskTypeStationPresetFile(string Path, TaskTypeStationConfiguration Configuration);
