using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 按任务类型准入的不投运原因只在服务端与看板（control-server#160，规格 5.3）：经既有的
/// <c>/api/dashboard/dispatch-backlog</c> 带中文说明出现在派车积压里。
/// </summary>
public sealed class TaskTypeAdmissionBacklogDescriptionTests
{
    [Theory]
    [InlineData(DispatchReasonCodes.OutOfScopeWorkType)]
    [InlineData(DispatchReasonCodes.TaskTypeBindingMissing)]
    [InlineData(TaskTypeStationReasonCodes.BindingStationNotInCatalog)]
    [InlineData(DispatchReasonCodes.TaskTypeHeld)]
    [InlineData(DispatchReasonCodes.TaskTypeNotYetExecutable)]
    public async Task TheBacklogCarriesAChineseDescriptionForEveryTaskTypeAdmissionReason(string reason)
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset firstSeen = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = "D-1",
            TransportDemandKey = "TDK-D-1",
            FirstSeenAt = firstSeen,
            DemandCreatedAt = firstSeen.AddMinutes(-1),
            DecisionFingerprint = "fingerprint",
            ReasonCode = reason,
            LastSeenAt = firstSeen,
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        object fact = await new DispatchBacklogQueryEndpoint()
            .ReadAsync(fixture.Context, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(fact));

        JsonElement row = Assert.Single(document.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(reason, row.GetProperty("reasonCode").GetString());
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", row.GetProperty("reasonDescription").GetString());
    }
}
