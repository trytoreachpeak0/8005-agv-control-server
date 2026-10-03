using System.Collections.Concurrent;
using System.Data.Common;
using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 看板说明守卫（批次8-21，control-server#392，独立审查 S1）：这个类里每一条空闲返回执行用例跑出来的、写在空闲返回旅程行上的每一个
/// <c>BlockReasonCode</c>，都要有空闲返回口径的中文说明（<see cref="IdleReturnCodeDescriptions.DescribeJourneyCode"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 之前的守卫遍历手写的 <see cref="IdleReturnCodeDescriptions.AllJourneyCodes"/>，等于拿清单核清单：引擎改写一个新码，清单不知道，守卫照绿。
/// 这里改为收集实际跑出来的码：<see cref="FleetAsync"/> 搭的每个夹具都挂一个保存拦截器，看每次保存里新增或改了阻断码的空闲返回旅程行。
/// 用例结束时（<see cref="DisposeAsync"/>）断言没有一个码缺说明。
/// </para>
/// <para>
/// 覆盖面就是这个类的用例走到的路径：新码出现在一条没有用例走到的路径上时，这里看不见它——那条路径本来就该补用例。
/// </para>
/// </remarks>
public sealed partial class IdleReturnExecutionTests : IAsyncLifetime
{
    // xunit runs one class's tests one after another, so one recorder per test is enough.
    private static IdleReturnCodeRecorder? s_codes;

    public ValueTask InitializeAsync()
    {
        s_codes = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IdleReturnCodeRecorder? codes = s_codes;
        s_codes = null;
        if (codes is not null)
        {
            Assert.True(
                codes.Undescribed.IsEmpty,
                "Idle return journey codes written in this test with no idle return description on the dashboard: "
                + string.Join(", ", codes.Undescribed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>给这个测试搭的夹具挂上记录器；原来的命令拦截器（例如注入崩溃的 <see cref="FailingInsert"/>）照样生效。</summary>
    private static IdleReturnCodeRecorder RecordIdleReturnCodes(DbCommandInterceptor? inner)
    {
        s_codes = new IdleReturnCodeRecorder(inner);
        return s_codes;
    }

    /// <summary>
    /// 保存拦截器：每次保存前看空闲返回旅程行的阻断码，没有空闲返回口径说明的记下来。命令拦截那几个方法转给原来的拦截器。
    /// </summary>
    private sealed class IdleReturnCodeRecorder(DbCommandInterceptor? inner) : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public ConcurrentBag<string> Seen { get; } = [];

        public ConcurrentBag<string> Undescribed { get; } = [];

        public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Inspect(eventData.Context);
            return result;
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private void Inspect(DbContext? context)
        {
            if (context is null)
            {
                return;
            }
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<JourneyRuntimeRow> entry in
                     context.ChangeTracker.Entries<JourneyRuntimeRow>())
            {
                bool written = entry.State == EntityState.Added ||
                               (entry.State == EntityState.Modified &&
                                entry.Property(row => row.BlockReasonCode).IsModified);
                if (!written || !entry.Entity.IsIdleReturn() || entry.Entity.BlockReasonCode is not { } code)
                {
                    continue;
                }
                Seen.Add(code);
                if (string.IsNullOrWhiteSpace(IdleReturnCodeDescriptions.DescribeJourneyCode(code)))
                {
                    Undescribed.Add(code);
                }
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            inner?.ReaderExecuting(command, eventData, result) ?? result;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            inner?.ReaderExecutingAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            inner?.NonQueryExecuting(command, eventData, result) ?? result;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            inner?.NonQueryExecutingAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);
    }
}
