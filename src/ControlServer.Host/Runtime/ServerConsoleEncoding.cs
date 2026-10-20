using System.Text;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#812: the Host's redirected standard output and error are UTF-8 (no byte order mark), whatever the
/// system locale. .NET writes a redirected console in the console's code page, so the L2 and G3 harnesses' out.log
/// files held GBK on a zh-CN machine and a literal '?' for every Chinese character on an en-US one (code page 437),
/// lost before anything could read it back.
/// </summary>
/// <remarks>
/// Only the managed writers are replaced (<see cref="Console.SetOut"/>, <see cref="Console.SetError"/>), never
/// <see cref="Console.OutputEncoding"/>: that setter calls SetConsoleOutputCP, which changes the code page of a console
/// the Host shares with the shell that started it, and fails when there is no console at all, as under the service
/// control manager. A stream that is a console stays as it is: what reaches a console window is characters, not bytes
/// anyone reads back. In production the Host is a Windows service with no standard handles, which .NET reports as
/// redirected: the writers are replaced there too, over a null stream, and write nowhere, as before. Its log is the
/// Serilog File sink, UTF-8 already.
/// <para>
/// Not covered: what the runtime itself prints for an unhandled exception ("Unhandled exception. ...") is written in the
/// console's code page without going through <see cref="Console.Error"/>, so on an en-US machine its Chinese is still
/// '?' in the redirected stderr.
/// </para>
/// </remarks>
public static class ServerConsoleEncoding
{
    public static void UseUtf8WhenRedirected()
    {
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }
        if (Console.IsErrorRedirected)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
    }
}
