using System.Reflection;
using System.Text.Json;
using Xunit.v3;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the <c>IntegrationSlice</c> trait: every value is one of the sixteen slice
/// ids protocol v2 froze, running the sixteen filters leaves no traited test behind, and every test
/// that carries no slice at all is accounted for by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a partition, and saying so is the point.</b> Ticket 14 asked for "the union of the
/// sixteen slices equals the whole set and pairwise intersections are empty". Measured against the
/// suite on 2026-09-08, both halves are false and neither can be made true without destroying
/// something real: 40 of the 441 test methods carry two to four slice traits, because a test that
/// proves a durable replay of a load correction is evidence for the reliable-delivery family and
/// the pickup family at once; and 300 carry no slice trait, because section 7.5 of the
/// full-product scope specification names ten classes of work that deliberately have none --
/// multi-vehicle execution, the route-graph engine, the RIoT command surface, the create gate, and
/// the cross-cutting architecture guards among them. Forcing a partition would mean deleting
/// traits that say something and inventing traits that do not.
/// </para>
/// <para>
/// <b>What is checkable is closure on the slice-bearing surface</b>, and that is what this class
/// asserts: no value outside the frozen sixteen (so no filter misses a test and no <c>W2G-IS-NN</c>
/// survives the v2 rename), no vector claimed by a test whose own slices do not contain it (so a
/// slice filter runs everything that claims to prove that slice's vectors), and an explicit ledger
/// of what sits outside the family, which fails when the set changes rather than drifting.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster</b>, for the reason
/// <see cref="ProtocolVectorTestBindingArchitectureTests"/> gives.
/// </para>
/// </remarks>
public sealed class IntegrationSliceTraitArchitectureTests
{
    private const string SliceTrait = "IntegrationSlice";

    private const string VectorTrait = "ProtocolVector";

    /// <summary>
    /// Test classes in which no test carries a slice trait, each with why it sits outside the
    /// family.
    /// </summary>
    /// <remarks>
    /// The reasons are section 7.5's, not new ones: single-ended work has no cross-end slice to
    /// belong to, and a cross-cutting guard hung off a slice goes dark whenever that slice is
    /// deferred. An entry here is a claim that can be argued with, which is the difference between
    /// a ledger and a count.
    /// </remarks>
    private static readonly SortedDictionary<string, string> ClassesOutsideTheSliceFamily =
        new(StringComparer.Ordinal)
        {
            ["AgvRestorationTests"] = "batch 3 FP-C5 archive-and-restore lifecycle; server-internal, no wire message",
            ["Batch2CapabilityStoresTests"] = "batch 2 track B persistence foundation; server-internal, no wire message",
            ["Batch3MigrationDisciplineTests"] = "cross-cutting migration and startup guard; hanging it off a slice would defer the guard with the slice",
            ["CreateGateTests"] = "7.5 #9, FP-C13 create gate and catalog availability; server-internal gate",
            ["DashboardSkeletonTests"] = "batch 3 FP-C8 dashboard skeleton; the dashboard is disjoint from the protocol by construction and reads only /api/dashboard/",
            ["EmergencyStopSupervisorTests"] = "7.5 #8, FP-C11 fault isolation; single-ended server-to-RIoT",
            ["ExperimentalRiotCreateGateTests"] = "RIoT create experiment; single-ended server-to-RIoT",
            ["ExperimentalRiotCreateMigrationTests"] = "RIoT create experiment migration; server-internal",
            ["GovernanceSnapshotAndAuditTests"] = "batch 3 FP-C7/FP-C5 shared snapshot and audit mechanism; server-internal, no wire message",
            ["IntegrationSliceTraitArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["MapStationResolverTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["MultiVehicleExecutionTests"] = "7.5 #1, FP-C2 B2 multi-vehicle; the conformance vector format has no vehicle dimension",
            ["OnboardVehicleSafetyEndpointsTests"] = "server-side safety projection endpoint; not a protocol wire message",
            ["PackageCapacityStoreTests"] = "server-internal store; no wire message",
            ["ProtocolIdentityArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolMessageSurfaceArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolPayloadShapeArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["ProtocolVectorTestBindingArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["RiotAbsentAtObservationCreateExperimentOptionsTests"] = "options validation; no wire message",
            ["RiotCallAllowlistArchitectureTests"] = "cross-cutting architecture guard; hanging it off a slice would defer the guard with the slice",
            ["RiotCreateDispatchGateOptionsTests"] = "options validation; no wire message",
            ["RiotDispatchAuditTests"] = "7.5 #8, FP-C11 order command surface; single-ended server-to-RIoT",
            ["RiotOrderCommandSurfaceTests"] = "7.5 #8, FP-C11 order command surface; single-ended server-to-RIoT",
            ["RiotSdkPackageProvenanceTests"] = "vendored SDK package provenance; not a protocol fact",
            ["RiotSdkRegistrationTests"] = "SDK composition-root wiring; no wire message",
            ["RollbackAndImpactPreviewTests"] = "batch 3 rollback and impact preview; a rollback is a new activation of an existing frozen version, server-internal",
            ["RouteGraphDispatchTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["RouteGraphEngineTests"] = "7.5 #6, RouteGraphSnapshot engine; single-ended server-to-RIoT",
            ["SlotConfigurationActivationTests"] = "batch 3 FP-IS-14 server half; SlotConfigurationActivationCommand and SlotConfigurationActivationResult are still pinned in MessagesWithoutAnImplementation on this line, so nothing here stands behind a wire message yet",
            ["SlotConfigurationAuthorityTests"] = "batch 3 FP-C7 slot configuration authority; two-layer versioning inside the server, no wire message",
            ["SlotConfigurationGateModeTests"] = "readiness gate mode validation; no wire message",
            ["SlotConfigurationReadinessGateTests"] = "batch 3 per-vehicle IO integrity gate; a server-side readiness predicate, no wire message",
            ["VehicleFaultIsolationTests"] = "7.5 #8, FP-C11 fault isolation; single-ended server-to-RIoT"
        };

    /// <summary>
    /// Classes that are partly in the slice family, with how many of their tests are not, and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A count rather than a list of method names. The names would break on a rename, which teaches
    /// nothing; the count breaks when a test is <i>added</i> without a slice trait to a class where
    /// other tests have one, which is the case worth stopping. Each of these three splits is
    /// deliberate: the traited tests are the ones standing behind a wire message, the rest are the
    /// RIoT adapter's own behaviour or an options default.
    /// </para>
    /// <para>
    /// This is also the field that would have caught what the v2 retag found by hand:
    /// <c>OnboardJourneyPublisherTests</c> was in this list with one entry, and that one test was
    /// <c>SlotOperationRejectsInvalidCorrelationAndSlotOrderBeforePersistence</c> -- the negative
    /// counterpart of a test carrying four slice traits, sitting in no slice at all. It carries
    /// <c>FP-IS-02</c> and <c>FP-IS-04</c> now and the class is gone from here.
    /// </para>
    /// </remarks>
    private static readonly SortedDictionary<string, (int Count, string Reason)> SliceBearingClassesWithTestsOutside =
        new(StringComparer.Ordinal)
        {
            ["FakeRiotTests"] = (8, "the fake's own control plane and conflict behaviour; the traited tests are the shapes the production adapter parses"),
            ["HttpRiotMovementGatewayTests"] = (20, "RIoT adapter fail-closed and sanitisation behaviour; the traited tests are the ones a wire message depends on"),
            ["JourneyRuntimeOptionsTests"] = (2, "option defaults; the traited tests are the validations that fail a deployment closed"),
            ["OnboardAlarmProjectionTests"] = (1, "the dashboard self-registration convention #12 set up; the traited tests are the ones standing behind OnboardAlarmSnapshot")
        };

    private sealed record TestMethod(string ClassName, string MethodName, string[] Slices, string[] Vectors);

    /// <summary>
    /// Every slice trait names one of the sixteen ids the protocol froze.
    /// </summary>
    /// <remarks>
    /// This is the leg that catches a <c>W2G-IS-NN</c> the v2 rename missed, a typo, and a
    /// seventeenth slice somebody invented -- each of which is a test no slice filter would ever
    /// run. The ids come from the vendored index rather than a pattern, so the check is against
    /// what the protocol actually froze and not merely against a shape that looks right.
    /// </remarks>
    [Fact]
    public void EverySliceTraitNamesOneOfTheSixteenFrozenSlices()
    {
        HashSet<string> frozen = new(FrozenSliceIds(), StringComparer.Ordinal);
        Assert.Equal(16, frozen.Count);

        string[] offences =
        [
            .. TestMethods()
                .SelectMany(test => test.Slices.Select(slice => (test, slice)))
                .Where(pair => !frozen.Contains(pair.slice))
                .Select(pair => $"{pair.test.ClassName}.{pair.test.MethodName} carries {pair.slice}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "Tests carry an " + SliceTrait + " trait naming a slice the protocol did not freeze, so "
            + "no slice filter runs them: " + string.Join("; ", offences));
    }

    /// <summary>
    /// Running the sixteen slice filters leaves no traited test behind.
    /// </summary>
    /// <remarks>
    /// The direct reading of "the union of the sixteen loses nothing", stated over the tests rather
    /// than over the ids. It overlaps the check above by construction and reports differently: that
    /// one names the offending trait value, this one names the test that would silently stop being
    /// gate evidence.
    /// </remarks>
    [Fact]
    public void TheSixteenSliceFiltersRunEveryTestThatCarriesTheTrait()
    {
        HashSet<string> frozen = new(FrozenSliceIds(), StringComparer.Ordinal);
        TestMethod[] traited = [.. TestMethods().Where(test => test.Slices.Length > 0)];
        Assert.NotEmpty(traited);

        string[] reachedByNoFilter =
        [
            .. traited
                .Where(test => !test.Slices.Any(frozen.Contains))
                .Select(test => $"{test.ClassName}.{test.MethodName}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            reachedByNoFilter.Length == 0,
            "These tests carry an " + SliceTrait + " trait that no frozen slice filter selects: "
            + string.Join(", ", reachedByNoFilter));

        Assert.Equal(
            traited.Length,
            frozen.SelectMany(slice => traited.Where(test => test.Slices.Contains(slice, StringComparer.Ordinal)))
                .Distinct()
                .Count());
    }

    /// <summary>
    /// Every vector a test claims belongs to a slice that same test carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanised half of "review each test as you retag it". A test claiming
    /// <c>CV-EXCEPTION-RESUME</c> while filed under <c>FP-IS-05</c> and <c>FP-IS-06</c> is not
    /// wrong about what it proves -- it is missing <c>FP-IS-07</c>, the family the protocol files
    /// that vector under, and <c>CONTROL_SERVER_G2</c> for <c>FP-IS-07</c> would not have run it.
    /// The v2 retag found eight such tests and closed them by adding the slice, never by removing
    /// the vector.
    /// </para>
    /// <para>
    /// Deliberately one-directional. A test may carry slices beyond its vectors' -- that is how a
    /// single durable-replay test stands behind several families -- so the reverse would be false
    /// of a suite that is doing nothing wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryClaimedVectorIsCoveredByASliceItsOwnTestCarries()
    {
        Dictionary<string, string[]> vectorsBySlice = VectorsBySlice();

        string[] offences =
        [
            .. TestMethods()
                .SelectMany(test =>
                {
                    HashSet<string> covered = new(
                        test.Slices.SelectMany(slice => vectorsBySlice.GetValueOrDefault(slice, [])),
                        StringComparer.Ordinal);

                    return test.Vectors
                        .Where(vector => !covered.Contains(vector))
                        .Select(vector =>
                            $"{test.ClassName}.{test.MethodName} claims {vector} but carries only "
                            + (test.Slices.Length == 0 ? "no slice" : string.Join("/", test.Slices.Order(StringComparer.Ordinal))));
                })
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "These tests prove a frozen vector without being filed under any slice that vector "
            + "belongs to, so that slice's gate run would not execute them: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Every test outside the slice family is accounted for, and the ledger holds nothing stale.
    /// </summary>
    [Fact]
    public void EveryTestOutsideTheSliceFamilyIsAccountedFor()
    {
        TestMethod[] tests = TestMethods();
        Assert.NotEmpty(tests);

        ILookup<string, TestMethod> byClass = tests.ToLookup(test => test.ClassName, StringComparer.Ordinal);

        Assert.Empty(ClassesOutsideTheSliceFamily.Keys
            .Intersect(SliceBearingClassesWithTestsOutside.Keys, StringComparer.Ordinal));

        string[] unaccounted = Unaccounted(tests);

        Assert.True(
            unaccounted.Length == 0,
            "These tests carry no " + SliceTrait + " trait and no ledger entry says why: "
            + string.Join(", ", unaccounted));

        string[] wronglyLedgered =
        [
            .. ClassesOutsideTheSliceFamily.Keys
                .Where(name => !byClass[name].Any() || byClass[name].Any(test => test.Slices.Length > 0))
                .Select(name => byClass[name].Any()
                    ? $"{name} now has tests inside the slice family"
                    : $"{name} has no tests at all")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            wronglyLedgered.Length == 0,
            "These classes are ledgered as wholly outside the slice family and are not: "
            + string.Join("; ", wronglyLedgered));

        string[] countDrift =
        [
            .. SliceBearingClassesWithTestsOutside
                .Select(entry => (entry.Key, entry.Value.Count,
                    Actual: byClass[entry.Key].Count(test => test.Slices.Length == 0),
                    Traited: byClass[entry.Key].Count(test => test.Slices.Length > 0)))
                .Where(row => row.Actual != row.Count || row.Traited == 0)
                .Select(row => row.Traited == 0
                    ? $"{row.Key} no longer has any test inside the slice family"
                    : $"{row.Key} has {row.Actual} tests outside the family, ledgered as {row.Count}")
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            countDrift.Length == 0,
            "The ledger of partly-outside classes no longer matches the suite: "
            + string.Join("; ", countDrift));
    }

    /// <summary>
    /// Proves the accounting check is not vacuous, by running it over a suite in which a real test
    /// lost its only slice trait.
    /// </summary>
    /// <remarks>
    /// A real test rather than a fabricated one:
    /// <see cref="OnboardMessageProcessorTests.ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession"/>
    /// carries exactly one slice trait today, so stripping it reproduces what a careless edit would
    /// do. The first assertion is the load-bearing half -- it says the real suite is reported clean
    /// by the same computation that reports the damaged one, rather than by an emptier one.
    /// </remarks>
    [Fact]
    public void TheAccountingCheckReportsATestThatLosesItsOnlySliceTrait()
    {
        TestMethod[] tests = TestMethods();
        const string victim = nameof(
            OnboardMessageProcessorTests.ProtocolProblemIsRecordedWithoutAnsweringOrDroppingTheSession);
        TestMethod real = Assert.Single(tests, test => test.MethodName == victim);
        Assert.Single(real.Slices);

        Assert.Empty(Unaccounted(tests));
        Assert.Equal(
            [$"{real.ClassName}.{real.MethodName}"],
            Unaccounted([.. tests.Where(test => test != real), real with { Slices = [] }]));
    }

    private static string[] Unaccounted(IEnumerable<TestMethod> tests) =>
    [
        .. tests
            .Where(test => test.Slices.Length == 0
                && !ClassesOutsideTheSliceFamily.ContainsKey(test.ClassName)
                && !SliceBearingClassesWithTestsOutside.ContainsKey(test.ClassName))
            .Select(test => $"{test.ClassName}.{test.MethodName}")
            .Order(StringComparer.Ordinal)
    ];

    private static TestMethod[] TestMethods() =>
    [
        .. typeof(IntegrationSliceTraitArchitectureTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(RunsAsATest)
            .Select(method =>
            {
                TraitAttribute[] traits =
                [
                    .. method.GetCustomAttributes<TraitAttribute>()
                        .Concat(method.DeclaringType!.GetCustomAttributes<TraitAttribute>())
                ];

                return new TestMethod(
                    method.DeclaringType!.Name,
                    method.Name,
                    [
                        .. traits.Where(trait => string.Equals(trait.Name, SliceTrait, StringComparison.Ordinal))
                            .Select(trait => trait.Value)
                    ],
                    [
                        .. traits.Where(trait => string.Equals(trait.Name, VectorTrait, StringComparison.Ordinal))
                            .Select(trait => trait.Value)
                    ]);
            })
    ];

    private static bool RunsAsATest(MethodInfo method) => method
        .GetCustomAttributes()
        .OfType<IFactAttribute>()
        .Any(fact => fact.Skip is null);

    private static string[] FrozenSliceIds() => [.. VectorsBySlice().Keys.Order(StringComparer.Ordinal)];

    private static Dictionary<string, string[]> VectorsBySlice()
    {
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "vendor", "8005-agv-protocol", "integration-slices", "index.json")));

        return index.RootElement.GetProperty("slices").EnumerateArray().ToDictionary(
            slice => slice.GetProperty("integrationSliceId").GetString()!,
            string[] (slice) =>
            [
                .. slice.GetProperty("vectorIds").EnumerateArray().Select(vector => vector.GetString()!)
            ],
            StringComparer.Ordinal);
    }
}
