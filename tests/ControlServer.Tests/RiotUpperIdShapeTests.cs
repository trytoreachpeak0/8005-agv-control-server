using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

/// <summary>
/// control-server#616: every upperId this server sends RIoT fits <see cref="RiotUpperIds.MaxLength"/> with the device keys real
/// vehicles have (<c>BROKERX-</c> and 32 hex digits, 40 characters), stays unique, and keeps the own prefix.
/// </summary>
/// <remarks>
/// The tests before this one used <c>BROKERX-0001</c>, 12 characters, so the idle-return and charge upperIds that carried the
/// whole device key were 44 to 46 long in tests and 69 to 71 on the real vehicles, where RIoT refused them with <c>00002</c>.
/// </remarks>
public sealed class RiotUpperIdShapeTests
{
    /// <summary>Device keys read from the plant's RIoT on 2026-10-11 (cs566b evidence), plus the longest the shape allows.</summary>
    public static readonly string[] ProductionKeys =
    [
        "BROKERX-7daca4ee91da498d8026c68b7b941127",
        "BROKERX-f38975561adf46ccb1d2f23833c7d0e4",
        "BROKERX-ffffffffffffffffffffffffffffffff",
    ];

    private const string DemandId = "a1000566-0000-0000-0000-000000000001";

    private static readonly DateTimeOffset Instant = new(2026, 10, 11, 15, 0, 58, 123, TimeSpan.Zero);

    public static TheoryData<string, string, long, int> RealisticShapes()
    {
        TheoryData<string, string, long, int> data = [];
        foreach (string key in ProductionKeys)
        {
            foreach (string shape in Shapes)
            {
                // Generations, rebuild ordinals and clearance attempts well past anything seen: generation 1 is every order so
                // far, REBUILD-1-2 the highest rebuild, CLR61 the most clearance attempts (an L2 run, evidence/cs409).
                data.Add(shape, key, 99, 99);
            }
        }
        return data;
    }

    public static TheoryData<string, string, long, int> ExtremeShapes()
    {
        TheoryData<string, string, long, int> data = [];
        foreach (string shape in Shapes)
        {
            data.Add(shape, ProductionKeys[^1], long.MaxValue, int.MaxValue);
        }
        return data;
    }

    private static readonly string[] Shapes = ["PICKUP", "GATE", "REBUILD", "CHARGE", "IDLE", "CLEARANCE"];

    /// <summary>The guard that was missing: each kind of upperId, with a real vehicle's device key, fits the limit.</summary>
    [Theory]
    [MemberData(nameof(RealisticShapes))]
    public void EveryUpperIdFitsTheLimitWithAProductionShapedDeviceKey(string shape, string vehicleKey, long generation, int ordinal)
    {
        string upperId = UpperIdOf(shape, vehicleKey, generation, ordinal);

        Assert.True(upperId.Length <= RiotUpperIds.MaxLength, $"{upperId} is {upperId.Length} characters");
    }

    /// <summary>No value the numbers can take pushes an upperId past the limit: it is a bound, not a guess about how far they go.</summary>
    [Theory]
    [MemberData(nameof(ExtremeShapes))]
    public void EveryUpperIdFitsTheLimitWhateverItsNumbers(string shape, string vehicleKey, long generation, int ordinal)
    {
        string upperId = UpperIdOf(shape, vehicleKey, generation, ordinal);

        Assert.True(upperId.Length <= RiotUpperIds.MaxLength, $"{upperId} is {upperId.Length} characters");
    }

    /// <summary>
    /// The limit is the longest upperId RIoT is known to have accepted. Raising it needs RIoT's real limit from a document, its
    /// SDK or the behaviour lab; this pins that it has not drifted upwards on a guess.
    /// </summary>
    [Fact]
    public void TheLimitIsTheLongestUpperIdRiotIsKnownToHaveAccepted()
    {
        int limit = RiotUpperIds.MaxLength;
        Assert.Equal("W2G-a1000566-0000-0000-0000-000000000001-REBUILD-1-1".Length, limit);
    }

    /// <summary>
    /// A demand's orders keep their shape exactly: the transport orders already on RIoT when this ships were created under it,
    /// and a person reads <c>-PICKUP-</c>, <c>-GATE-</c>, <c>-REBUILD-</c> off them (docs/vehicle-fault-clearance-field-guide.md).
    /// </summary>
    [Fact]
    public void ADemandsUpperIdsKeepTheShapeTheyHadAtTheGenerationsInUse()
    {
        Assert.Equal(
            [$"W2G-{DemandId}-PICKUP-1", $"W2G-{DemandId}-GATE-1", $"W2G-{DemandId}-REBUILD-1-1", $"W2G-{DemandId}-REBUILD-1-2"],
            [
                RiotUpperIds.Pickup(DemandId, 1), RiotUpperIds.Gate(DemandId, 1), RiotUpperIds.Rebuild(DemandId, 1, 1),
                RiotUpperIds.Rebuild(DemandId, 1, 2),
            ]);
    }

    /// <summary>
    /// An upperId too long for RIoT falls back to <c>W2G-H-</c> and a hash of it, and the same input gives the same upperId every
    /// time: a retry or a restart that rebuilds it must reconcile the order it already created, not create a second one (REQ-0283).
    /// </summary>
    [Fact]
    public void AnUpperIdThatFallsBackToTheHashedShapeIsTheSameEveryTime()
    {
        string first = RiotUpperIds.Rebuild(DemandId, 10, 1);
        string second = RiotUpperIds.Rebuild(DemandId, 10, 1);

        Assert.Equal(first, second);
        Assert.StartsWith("W2G-H-", first, StringComparison.Ordinal);
        Assert.Equal(38, first.Length);
        Assert.NotEqual(first, RiotUpperIds.Rebuild(DemandId, 10, 2));
    }

    /// <summary>
    /// What a person reads off a charge, idle-return or clearance upperId survives the shortening: the kind, and the instant the
    /// journey was committed. The device key itself is on the RIoT order already (<c>appointVehicleKey</c>).
    /// </summary>
    [Fact]
    public void AVehicleJourneysUpperIdStillNamesItsKindAndItsInstant()
    {
        string key = ProductionKeys[0];

        string charge = ChargingIdentity.UpperIdFor(ChargingIdentity.JourneyIdFor(key, Instant));
        string idle = IdleReturnJourneyShape.UpperIdFor(IdleReturnIdentity.JourneyIdFor(key, Instant));
        string clearance = ClearanceMoveShape.UpperIdFor(ChargingIdentity.JourneyIdFor(key, Instant), 3);

        Assert.Matches("^W2G-CHARGE-[0-9a-f]{8}-20261011T150058123Z$", charge);
        Assert.Matches("^W2G-IDLE-[0-9a-f]{8}-20261011T150058123Z$", idle);
        Assert.Matches("^W2G-CHARGE-[0-9a-f]{8}-20261011T150058123Z-CLR3$", clearance);
    }

    /// <summary>
    /// The same vehicle in the same millisecond: a charge, an idle return and every clearance attempt each get an upperId of
    /// their own, and the same journey always gets the same one (REQ-0283: the upperId never changes across retries).
    /// </summary>
    [Fact]
    public void OneVehicleInOneMillisecondNeverSharesAnUpperIdAcrossKindsAndIsStableForOneJourney()
    {
        string key = ProductionKeys[1];
        string chargeJourney = ChargingIdentity.JourneyIdFor(key, Instant);
        string idleJourney = IdleReturnIdentity.JourneyIdFor(key, Instant);
        string[] upperIds =
        [
            ChargingIdentity.UpperIdFor(chargeJourney),
            IdleReturnJourneyShape.UpperIdFor(idleJourney),
            .. Enumerable.Range(1, 120).Select(attempt => ClearanceMoveShape.UpperIdFor(chargeJourney, attempt)),
        ];

        Assert.Equal(upperIds.Length, upperIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ChargingIdentity.UpperIdFor(chargeJourney), ChargingIdentity.UpperIdFor(ChargingIdentity.JourneyIdFor(key, Instant)));
        Assert.Equal(IdleReturnJourneyShape.UpperIdFor(idleJourney), IdleReturnJourneyShape.UpperIdFor(IdleReturnIdentity.JourneyIdFor(key, Instant)));
    }

    /// <summary>
    /// Another vehicle's order is never taken for this one's: this server knows its own orders by the upperId stored on the
    /// intent, so two vehicles committing in the same millisecond must not end up with the same one. The keys include ones that
    /// differ in a single hex digit, and a fleet's worth of others.
    /// </summary>
    [Fact]
    public void TwoVehiclesInOneMillisecondNeverShareAnUpperId()
    {
        string[] keys =
        [
            .. ProductionKeys,
            "BROKERX-fffffffffffffffffffffffffffffffe",
            .. Enumerable.Range(0, 64).Select(index => "BROKERX-" + index.ToString("x32", CultureInfo.InvariantCulture)),
        ];
        string[] upperIds =
        [
            .. keys.SelectMany(key => new[]
            {
                ChargingIdentity.UpperIdFor(ChargingIdentity.JourneyIdFor(key, Instant)),
                IdleReturnJourneyShape.UpperIdFor(IdleReturnIdentity.JourneyIdFor(key, Instant)),
                ClearanceMoveShape.UpperIdFor(ChargingIdentity.JourneyIdFor(key, Instant), 1),
            }),
        ];

        Assert.Equal(upperIds.Length, upperIds.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every upperId keeps the own prefix, at any value: an own order whose intent is lost is then held as unproven rather than
    /// cancelled as foreign (<see cref="ForeignRunningOrders.OwnUpperIdPrefix"/>, REQ-0164).
    /// </summary>
    [Theory]
    [MemberData(nameof(RealisticShapes))]
    [MemberData(nameof(ExtremeShapes))]
    public void EveryUpperIdStartsWithTheOwnPrefix(string shape, string vehicleKey, long generation, int ordinal)
    {
        Assert.Equal(ForeignRunningOrders.OwnUpperIdPrefix, RiotUpperIds.Prefix);
        Assert.StartsWith(ForeignRunningOrders.OwnUpperIdPrefix, UpperIdOf(shape, vehicleKey, generation, ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// The production gateway against the fake RIoT over HTTP, which answers an upperId longer than RIoT is known to take the way
    /// RIoT did on 2026-10-11 (HTTP 200, business code <c>00002</c>, nothing created): each kind of order with a real vehicle's key
    /// is created.
    /// </summary>
    [Theory]
    [InlineData("CHARGE")]
    [InlineData("IDLE")]
    [InlineData("CLEARANCE")]
    [InlineData("REBUILD")]
    public async Task EachKindOfOrderForARealVehicleIsCreatedByTheFakeRiot(string shape)
    {
        string key = ProductionKeys[1];
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        string upperId = UpperIdOf(shape, key, 10, 1);

        RiotOrderObservation created = await gateway.CreateAsync(
            new OrderIntent("LEG-" + shape, null, upperId, "TO_WAITING_POINT", "WP-214", Instant, key, 26, 214, 1, 1, OrderShapes.SingleMove),
            TestContext.Current.CancellationToken);

        Assert.Equal((RiotOrderObservationKind.Active, null), (created.Kind, created.Receipt?.BusinessCode));
    }

    /// <summary>The fake refuses an over-long upperId the way RIoT does, so the test above can go red.</summary>
    [Fact]
    public async Task TheFakeRiotRefusesAnUpperIdLongerThanTheLimitWith00002()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        string upperId = "W2G-IDLE-BROKERX-f38975561adf46ccb1d2f23833c7d0e4-20261011T150058123Z";

        RiotOrderObservation created = await fixture.Gateway().CreateAsync(
            new OrderIntent("LEG-1", null, upperId, "TO_WAITING_POINT", "WP-214", Instant, ProductionKeys[1], 26, 214, 1, 1, OrderShapes.SingleMove),
            TestContext.Current.CancellationToken);

        Assert.Equal((69, RiotOrderObservationKind.Unknown, "00002"), (upperId.Length, created.Kind, created.Receipt?.BusinessCode));
    }

    private static string UpperIdOf(string shape, string vehicleKey, long generation, int ordinal) => shape switch
    {
        "PICKUP" => RiotUpperIds.Pickup(DemandId, generation),
        "GATE" => RiotUpperIds.Gate(DemandId, generation),
        "REBUILD" => RiotUpperIds.Rebuild(DemandId, generation, ordinal),
        "CHARGE" => ChargingIdentity.UpperIdFor(ChargingIdentity.JourneyIdFor(vehicleKey, Instant)),
        "IDLE" => IdleReturnJourneyShape.UpperIdFor(IdleReturnIdentity.JourneyIdFor(vehicleKey, Instant)),
        "CLEARANCE" => ClearanceMoveShape.UpperIdFor(ChargingIdentity.JourneyIdFor(vehicleKey, Instant), ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };
}
