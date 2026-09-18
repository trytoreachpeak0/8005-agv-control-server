using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ControlServer.Host.Composition;

/// <summary>
/// Turns "a background service threw and the host stopped itself" into a failure the service
/// control manager can see.
/// </summary>
/// <remarks>
/// <para>
/// The host keeps <see cref="BackgroundServiceExceptionBehavior.StopHost"/>: a background service
/// that has died leaves the process up but unable to do its job -- a dead
/// <c>OnboardTcpServer</c> accepts no vehicle, a dead <c>JourneyRuntimeWorker</c> advances no
/// journey -- while <c>/health/live</c> still answers. A fresh process is the only state known to
/// be whole, so the host stops and the service recovery policy the install and upgrade scripts
/// set starts it again.
/// </para>
/// <para>
/// That policy only fires on a failure. Left alone, <c>WindowsServiceLifetime</c> reports such a
/// stop as a clean one -- exit code 0 and "Service stopped successfully." -- which is exactly
/// what happened on 2026-09-18 at 22:39:13 and why the service stayed down until someone started
/// it by hand. This reporter runs when the host begins stopping; if a background service has
/// faulted by then, the stop was caused by it, and the service exit code becomes non-zero before
/// SERVICE_STOPPED is reported. <c>sc failureflag 1</c> then counts it as a failure.
/// </para>
/// <para>
/// A stop requested from outside (<c>Stop-Service</c>, <c>Set-JourneyRuntime.ps1</c>, the
/// close-gates watcher) finds no faulted service at that moment and leaves the exit code at 0, so
/// the recovery policy never restarts a service somebody stopped on purpose.
/// </para>
/// </remarks>
internal static partial class BackgroundServiceFaultReporting
{
    internal const int FaultedExitCode = 1;

    // Leaves room under the 31,839-character event log message limit.
    private const int MaximumEventLogMessageLength = 30_000;

    internal static IHost UseBackgroundServiceFaultReporting(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => Report(host.Services));
        return host;
    }

    internal static void Report(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        List<(BackgroundService Service, Exception Error)> faults = [];
        foreach (IHostedService hosted in services.GetServices<IHostedService>())
        {
            if (hosted is BackgroundService { ExecuteTask: { IsFaulted: true } task } background)
            {
                faults.Add((background, task.Exception!.GetBaseException()));
            }
        }
        if (faults.Count == 0)
        {
            return;
        }

        ILogger logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(BackgroundServiceFaultReporting).FullName!);
        foreach ((BackgroundService service, Exception error) in faults)
        {
            LogBackgroundServiceFaulted(logger, error, service.GetType().Name, FaultedExitCode);
        }

        Environment.ExitCode = FaultedExitCode;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (services.GetService<IHostLifetime>() is not ServiceBase windowsService)
        {
            return;
        }
        windowsService.ExitCode = FaultedExitCode;

        // The file log is not enough on its own: on 2026-09-18 it had stopped at its size limit two
        // hours before the stop it would have explained. The event log is also where anyone looking
        // at the SCM entries already is.
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            return;
        }
        try
        {
            windowsService.EventLog.WriteEntry(
                DescribeForEventLog(faults),
                EventLogEntryType.Error,
                eventID: 9001);
        }
        catch (Exception eventLogError) when (eventLogError is not OutOfMemoryException)
        {
            LogEventLogWriteFailed(logger, eventLogError);
        }
    }

    internal static string DescribeForEventLog(IReadOnlyList<(BackgroundService Service, Exception Error)> faults)
    {
        ArgumentNullException.ThrowIfNull(faults);

        System.Text.StringBuilder message = new();
        message.Append("A background service failed and stopped the host; the service exits with code ")
            .Append(FaultedExitCode)
            .AppendLine(" so the service recovery policy restarts it.");
        foreach ((BackgroundService service, Exception error) in faults)
        {
            message.AppendLine().Append(service.GetType().FullName).Append(": ").AppendLine(error.ToString());
        }
        return message.Length <= MaximumEventLogMessageLength
            ? message.ToString()
            : message.ToString(0, MaximumEventLogMessageLength);
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Error,
        Message = "Background service {Service} failed and stopped the host; exiting with code {ExitCode} so the service recovery policy restarts it.")]
    private static partial void LogBackgroundServiceFaulted(ILogger logger, Exception error, string service, int exitCode);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning,
        Message = "The background service failure could not be written to the Windows Application event log.")]
    private static partial void LogEventLogWriteFailed(ILogger logger, Exception error);
}
