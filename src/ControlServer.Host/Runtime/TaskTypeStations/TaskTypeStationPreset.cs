using ControlServer.Application;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>
/// 任务类型规则与按图绑定的受控预置配置（control-server#159）。
/// </summary>
public static class TaskTypeStationPreset
{
    public const string FileName = "task-type-stations.settings.json";
    public const string SectionName = "TaskTypeStations";
    public const string SettingsFileKey = "TaskTypeStations:settingsFile";

    public static TaskTypeStationPresetFile? Load(string baseDirectory, string? settingsFile) =>
        throw new NotImplementedException();
}

/// <summary>读到的预置配置与它来自哪个文件。</summary>
public sealed record TaskTypeStationPresetFile(string Path, TaskTypeStationConfiguration Configuration);
