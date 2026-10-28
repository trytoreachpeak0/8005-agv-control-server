using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 「这辆车还没答的出发请求」在生产规模的库上是毫秒级（control-server#874）。引擎每轮、每辆车读一次
/// （<see cref="CurrentStopDepartures.UnansweredAsync"/>）；10-27 生产库上它对每条出发请求相关子查询扫整张发件箱并 <c>json_extract</c>，
/// 一辆车 8,618 ms、一轮约 35 秒，录入判定、站点与持货期限、派车、故障监看都跟着晚。
/// </summary>
/// <remarks>
/// <para>
/// 造数照 10-27 生产库：发件箱 41,600 行（平均约 1 KB，其中 400 行是出发答复），收件箱 420 条出发请求分在三辆车上（车号是中文，
/// 存下的那一行里被转义），另有一万条心跳。答过的 400 条都有答复，没答的只有几条。
/// </para>
/// <para>
/// 判据：读回的行与预期一模一样；读用到的每条语句的查询计划里没有对表的 <c>SCAN</c>（只许 <c>json_each</c> 那种虚表）、没有相关子查询；
/// 每辆车一次读在阈值内。计划判据不看机器快慢，「恢复全表扫」的变异靠它稳定地红；计时阈值给 CI 留足余量。
/// </para>
/// </remarks>
public sealed class DepartureRequestAnswerQueryScaleTests
{
    private const int OutboxRows = 41_600;
    private const int AnsweredRequests = 400;
    private const int Heartbeats = 10_000;

    /// <summary>
    /// 一辆车一次读的上限，只是个天花板。改动前的全表扫在这份造数上本机约 180 ms（文件在系统缓存里是热的），生产上 8,618 ms，测试复现不出
    /// 生产那份读盘代价，所以分得出新旧代码的是计划判据，不是它。
    /// </summary>
    private static readonly TimeSpan PerVehicleBudget = TimeSpan.FromMilliseconds(250);

    private static readonly string[] Vehicles = ["一号车", "二号车", "三号车"];
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnansweredDepartureRequestsAreReadInMillisecondsOnAProductionSizedStore()
    {
        CommandLog commands = new();
        await using ScaleDatabase database = await ScaleDatabase.CreateAsync(commands);
        Dictionary<string, string[]> expected = await SeedAsync(database);
        // 预热：首次查询要编译、建计划，不该算进每车的计时（独立审查 L1）。用全答过的那辆车，收件箱与发件箱两条语句都走一遍。
        Assert.Empty(await CurrentStopDepartures.UnansweredAsync(database.Context, Vehicles[2], Token));

        foreach (string agvId in Vehicles)
        {
            commands.Reset();
            Stopwatch watch = Stopwatch.StartNew();
            IReadOnlyList<CurrentStopDepartures.Request> read =
                await CurrentStopDepartures.UnansweredAsync(database.Context, agvId, Token);
            watch.Stop();
            string[] plan = await database.ExplainAsync(commands.Commands);
            TestContext.Current.TestOutputHelper?.WriteLine(FormattableString.Invariant(
                $"{agvId}: rows={read.Count} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} statements={commands.Commands.Count} plan=[{string.Join(" | ", plan)}]"));

            Assert.Equal(expected[agvId], read.Select(request => request.DepartureRequestId));
            Assert.All(read, request => Assert.Equal(agvId, request.AgvId));
            // 每辆车都有请求，所以恰好两条：收件箱一条、发件箱按主键一条。少一条说明有语句没被看见，多了说明多读了。
            Assert.Equal(2, commands.Commands.Count);
            Assert.DoesNotContain(plan, detail =>
                detail.StartsWith("SCAN ", StringComparison.Ordinal) && !detail.Contains("VIRTUAL TABLE", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, detail => detail.Contains("CORRELATED", StringComparison.Ordinal));
            Assert.True(
                watch.Elapsed < PerVehicleBudget,
                $"{agvId} took {watch.Elapsed.TotalMilliseconds:F0} ms (budget {PerVehicleBudget.TotalMilliseconds:F0} ms).");
        }
    }

    /// <summary>
    /// 造数，返回每辆车应读回的 departureRequestId（按收件时刻）。一号车没答的两条之外还有一条同号重发（更晚、换了 messageId），只读回最早那条；
    /// 二号车没答一条；三号车全答过。
    /// </summary>
    private static async Task<Dictionary<string, string[]>> SeedAsync(ScaleDatabase database)
    {
        List<ProtocolInboxRow> inbox = [];
        List<(string MessageId, string MessageType, string PayloadJson)> outbox = [];
        DateTimeOffset at = Start;
        for (int index = 0; index < AnsweredRequests; index++)
        {
            string agvId = Vehicles[index % Vehicles.Length];
            string departureRequestId = Id("answered", index);
            at += TimeSpan.FromMinutes(20);
            inbox.Add(Request(Id("request", index), agvId, departureRequestId, at));
            outbox.Add(Result(agvId, departureRequestId, at));
        }
        string firstPending = Id("pending", 1);
        string secondPending = Id("pending", 2);
        string otherPending = Id("pending", 3);
        inbox.Add(Request(Id("pending-request", 1), Vehicles[0], firstPending, at + TimeSpan.FromSeconds(1)));
        inbox.Add(Request(Id("pending-request", 2), Vehicles[0], secondPending, at + TimeSpan.FromSeconds(2)));
        inbox.Add(Request(Id("pending-request", 3), Vehicles[0], firstPending, at + TimeSpan.FromSeconds(3)));
        inbox.Add(Request(Id("pending-request", 4), Vehicles[1], otherPending, at + TimeSpan.FromSeconds(4)));
        for (int index = 0; index < Heartbeats; index++)
        {
            inbox.Add(new ProtocolInboxRow
            {
                MessageId = Id("heartbeat", index),
                MessageType = "Heartbeat",
                RequestJson = JsonSerializer.Serialize(new
                {
                    messageType = "Heartbeat",
                    messageId = Id("heartbeat", index),
                    agvId = Vehicles[index % Vehicles.Length],
                    sessionGeneration = 1L,
                    payload = new { }
                }),
                ContentHash = new string('0', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Start + TimeSpan.FromSeconds(2 * index)
            });
        }
        // 其余发件箱行：清单、计划、业务快照一类，约 1 KB 一行，载荷里没有 departureRequestId。
        string padding = new('x', 900);
        string[] otherTypes = ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"];
        for (int index = outbox.Count; index < OutboxRows; index++)
        {
            string type = otherTypes[index % otherTypes.Length];
            outbox.Add((Id("outbox", index), type, JsonSerializer.Serialize(new
            {
                messageType = type,
                messageId = Id("outbox", index),
                agvId = Vehicles[index % Vehicles.Length],
                sessionGeneration = 1L,
                payload = new { revision = index, padding }
            })));
        }
        await database.SeedAsync(inbox, outbox);
        return new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Vehicles[0]] = [firstPending, secondPending],
            [Vehicles[1]] = [otherPending],
            [Vehicles[2]] = [],
        };
    }

    private static ProtocolInboxRow Request(string messageId, string agvId, string departureRequestId, DateTimeOffset receivedAt) => new()
    {
        MessageId = messageId,
        MessageType = CurrentStopDepartures.RequestType,
        RequestJson = JsonSerializer.Serialize(new
        {
            messageType = CurrentStopDepartures.RequestType,
            messageId,
            agvId,
            sessionGeneration = 1L,
            payload = new
            {
                departureRequestId,
                operationSessionId = Guid.NewGuid().ToString("D"),
                stationId = "305",
                worklistRevision = 3505L,
                @operator = new { operatorId = "OP-874", verificationMethod = "BADGE", verifiedAt = receivedAt }
            }
        }),
        ContentHash = new string('0', 64),
        FirstResponseJson = "{}",
        ReceivedAt = receivedAt
    };

    /// <summary>答复行与引擎写的一样：messageId 由 departureRequestId 派生，载荷带 departureRequestId。</summary>
    private static (string MessageId, string MessageType, string PayloadJson) Result(
        string agvId, string departureRequestId, DateTimeOffset at)
    {
        string messageId = CurrentStopDepartures.ResultMessageId(departureRequestId);
        return (messageId, CurrentStopDepartures.ResultType, JsonSerializer.Serialize(new
        {
            messageType = CurrentStopDepartures.ResultType,
            messageId,
            agvId,
            sessionGeneration = 1L,
            sentAt = at,
            payload = new
            {
                departureRequestId,
                outcome = "REJECTED",
                problem = new { reasonCode = "STATION_MISMATCH" },
                currentWorklistRevision = 3505L
            }
        }));
    }

    private static string Id(string kind, int index) => JourneyPlanBuilder.StableGuid($"cs874-{kind}", index.ToString(CultureInfo.InvariantCulture));

    /// <summary>记下读期间发出的每条语句与参数，事后拿去 <c>EXPLAIN QUERY PLAN</c>。</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<(string Text, (string Name, object? Value)[] Parameters)> Commands { get; } = [];

        public void Reset() => Commands.Clear();

        // 同步与异步、读行与读标量都记：只记异步读行时，改成同步 ToArray() 的整读就从计划判据底下溜过去了（独立审查 M1）。
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command) =>
            Commands.Add((command.CommandText,
                [.. command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, (object?)parameter.Value))]));
    }

    /// <summary>
    /// 临时目录里的 SQLite 文件库，建库与生产同一个模型。用文件而不用内存库：生产库在磁盘上、页缓存是默认的 2 MB，每扫一遍发件箱都要把它
    /// 从文件里重读一遍，内存库没有这一层，同一个全表扫在内存库上只要百来毫秒。
    /// </summary>
    private sealed class ScaleDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string path;

        private ScaleDatabase(SqliteConnection connection, ControlServerDbContext context, string path)
        {
            this.connection = connection;
            this.path = path;
            Context = context;
        }

        public ControlServerDbContext Context { get; }

        public static async Task<ScaleDatabase> CreateAsync(CommandLog commands)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cs874-scale-{Guid.NewGuid():N}.db");
            SqliteConnection connection = new($"Data Source={path};Pooling=False");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).AddInterceptors(commands).Options);
            await context.Database.EnsureCreatedAsync(Token);
            return new ScaleDatabase(connection, context, path);
        }

        public async Task SeedAsync(
            IReadOnlyList<ProtocolInboxRow> inbox,
            IReadOnlyList<(string MessageId, string MessageType, string PayloadJson)> outbox)
        {
            Context.ChangeTracker.AutoDetectChangesEnabled = false;
            Context.ProtocolInbox.AddRange(inbox);
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
            Context.ChangeTracker.AutoDetectChangesEnabled = true;

            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO "ProtocolOutbox" ("MessageId", "MessageType", "PayloadJson", "CreatedAt")
                VALUES ($id, $type, $payload, $createdAt)
                """;
            SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter type = insert.Parameters.Add("$type", SqliteType.Text);
            SqliteParameter payload = insert.Parameters.Add("$payload", SqliteType.Text);
            insert.Parameters.AddWithValue("$createdAt", Start.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            foreach ((string messageId, string messageType, string payloadJson) in outbox)
            {
                id.Value = messageId;
                type.Value = messageType;
                payload.Value = payloadJson;
                await insert.ExecuteNonQueryAsync(Token);
            }
            await transaction.CommitAsync(Token);
        }

        /// <summary>每条语句的查询计划（<c>detail</c> 列），按语句先后连起来。</summary>
        public async Task<string[]> ExplainAsync(IEnumerable<(string Text, (string Name, object? Value)[] Parameters)> commands)
        {
            List<string> details = [];
            foreach ((string text, (string Name, object? Value)[] parameters) in commands)
            {
                await using SqliteCommand explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN " + text;
                foreach ((string name, object? value) in parameters)
                {
                    explain.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                await using SqliteDataReader reader = await explain.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                {
                    details.Add(reader.GetString(3));
                }
            }
            return [.. details];
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            File.Delete(path);
        }
    }
}
