using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ControlServer.FieldOps;

internal static partial class Program
{
    internal const string SessionCommand = "session";

    /// <summary>
    /// 每条命令结束后单独一行的结束标记，后跟那条命令的退出码。命令自己打的东西（一个 JSON 对象、用法说明、异常）都在它之前。
    /// </summary>
    internal const string SessionExitMarker = "##fieldops-session-exit ";

    /// <summary>
    /// Windows 上 .NET 进程因未处理异常退出时的退出码（0xE0434352）。会话里一条命令抛出时报这个码，与单独起进程时看到的一样。
    /// </summary>
    internal const int UnhandledExceptionExitCode = unchecked((int)0xE0434352);

    /// <summary>
    /// 一个进程里依次执行多条命令（control-server#804）：L2 搭环境每个场景要调五六次本工具，每次新起进程约 2 秒，
    /// 大头是进程启动与 EF 模型构建，命令本身只写几行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 从 stdin 逐行读命令，每行一个 JSON 字符串数组，内容就是单独起进程时的命令行参数（动词在前，<c>--database</c> 照写）。
    /// 每条命令的输出照旧，之后单独一行 <see cref="SessionExitMarker"/> 加退出码。stderr 在会话里并进 stdout，
    /// 与调用方原先的 <c>2&gt;&amp;1</c> 一样按先后排在一起。stdin 读到结尾时会话以 0 退出。
    /// </para>
    /// <para>
    /// <b>每条命令在库上看到的与单独一个进程完全一样</b>：<see cref="RunCommandAsync"/> 每次新开 <c>DbContext</c>、
    /// 取一次当前时间；命令结束后清空 SQLite 连接池，进程活着也不留着库文件的句柄——单独起进程时，进程一退出这些连接就没了。
    /// 一条命令失败（退出码非零或抛异常）不结束会话，是否继续由调用方决定。
    /// </para>
    /// </remarks>
    private static async Task<int> SessionAsync(string[] args)
    {
        if (args.Length != 1)
        {
            return Usage($"'{SessionCommand}' takes no options; each command on stdin carries its own");
        }

        Console.SetError(Console.Out);
        using StreamReader input = new(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            int exitCode = await RunSessionLineAsync(line);
            SqliteConnection.ClearAllPools();
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{SessionExitMarker}{exitCode}"));
            Console.Out.Flush();
        }
        return 0;
    }

    private static async Task<int> RunSessionLineAsync(string line)
    {
        string[]? commandArgs;
        try
        {
            commandArgs = JsonSerializer.Deserialize<string[]>(line);
        }
        catch (JsonException)
        {
            commandArgs = null;
        }
        if (commandArgs is null || commandArgs.Any(argument => argument is null))
        {
            return Usage("a session line must be one JSON array of strings, the command's arguments");
        }
        if (commandArgs.Length > 0 && commandArgs[0] == SessionCommand)
        {
            return Usage("a session cannot start another session");
        }

        try
        {
            return await RunCommandAsync(commandArgs);
        }
        catch (Exception exception)
        {
            Console.Out.WriteLine("Unhandled exception. " + exception);
            return UnhandledExceptionExitCode;
        }
    }
}
