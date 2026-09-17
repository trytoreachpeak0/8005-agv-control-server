using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.v3;

namespace ControlServer.Tests;

/// <summary>
/// The machine guard on the binding between the protocol's frozen conformance vectors and this
/// server's named tests: every <c>vectorId</c> the protocol froze has a named test that runs, and
/// no test claims a <c>vectorId</c> the protocol never froze.
/// </summary>
/// <remarks>
/// <para>
/// "All thirty-one vectors are tested" was a sentence somebody counted by hand. v2 took the vector
/// set from 19 to 31 and the slice family table from 8 rows to 16, and nothing anywhere compared
/// those numbers against what the test suite actually covers -- the suite carried 187
/// <c>IntegrationSlice</c> traits and not one <c>vectorId</c>. A vector could be added to the
/// protocol, or a test proving one could be deleted, and every gate would stay green.
/// </para>
/// <para>
/// <b>The vector list is not copied here.</b>
/// <c>vendor/8005-agv-protocol/integration-slices/index.json</c> is the protocol's own file byte
/// for byte, and <see cref="ApprovedIndexSha256"/> pins it. The file lives in another repository
/// and CI checks out one repository, so a sibling directory is not reachable from the headless
/// runner -- the copy is how the list gets here at all, and the hash is what keeps it from
/// becoming a second, quietly diverging list. Refreshing it is
/// <c>vendor/8005-agv-protocol/README.md</c>.
/// </para>
/// <para>
/// <b>The binding is a trait, not a naming convention.</b> <c>Xunit.TraitAttribute</c> is sealed,
/// so a typed <c>[ProtocolVector]</c> attribute would have to reimplement <c>ITraitAttribute</c>
/// for nothing gained; and a trait earns something a bespoke attribute does not, in that
/// <c>dotnet test --filter "ProtocolVector=CV-SESSION-RECOVERY-HAPPY"</c> runs exactly the two
/// tests that claim to prove that vector. (This project runs xunit.v3 through
/// <c>xunit.runner.visualstudio</c> rather than the Microsoft Testing Platform -- CI passes
/// <c>--logger trx</c> -- so the filter is the VSTest expression, not <c>--filter-trait</c>.) The
/// <c>IntegrationSlice</c> trait is untouched and
/// orthogonal: a slice says which family a test belongs to, a vector says which frozen scenario it
/// proves, and one test commonly carries several of each.
/// </para>
/// <para>
/// <b>No <c>IntegrationSlice</c> trait, and no cluster.</b> Like
/// <see cref="RiotCallAllowlistArchitectureTests"/>, this is a cross-cutting guard rather than
/// evidence for a slice. Hanging it off a slice would mean the whole vector-to-test binding goes
/// unguarded for as long as that slice is deferred, which is precisely the failure it exists to
/// prevent.
/// </para>
/// </remarks>
public sealed class ProtocolVectorTestBindingArchitectureTests
{
    /// <summary>
    /// SHA-256 of the frozen slice family index, over the file's bytes. Taken from
    /// <c>8005-agv-protocol</c> commit <c>86575456c847041515b7b75e8851a00e0d939804</c> (branch
    /// <c>fp/v2-candidate</c>, the <c>2.0.0</c> candidate G1 passed on) on 2026-09-16. The file had
    /// not changed from its first freeze at <c>f6ee75d</c> on 2026-09-08 until this candidate added
    /// two vectors to <c>FP-IS-02</c>.
    /// </summary>
    private const string ApprovedIndexSha256 =
        "268ce62be4e0ec8fe6d26e2048a59cf1732f02713415a0c5f41b6b009951b6fc";

    /// <summary>
    /// The trait name a test uses to claim it proves a frozen vector.
    /// </summary>
    private const string VectorTrait = "ProtocolVector";

    /// <summary>
    /// The slices this line implements. Sequences 0 through 7 are <c>FP-IS-00</c> through
    /// <c>FP-IS-07</c>, batch 2 track A, recertified under v2; batch 3 adds <c>FP-IS-14</c> and
    /// <c>FP-IS-15</c>. The rest are scheduled into batches 6 through 11 by section 7.2 of the
    /// second edition of the full-product scope specification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was one integer -- "the last sequence this batch implements" -- while the implemented
    /// set was the contiguous run 0 through 7. Batch 3 breaks that shape: it lands 14 and 15
    /// without 8 through 13, so a bound stated as a sequence would either have to claim six slices
    /// nobody built or stop covering the two this batch does. A set says the same thing for a
    /// contiguous run and keeps saying it for a sparse one.
    /// </para>
    /// <para>
    /// The slice-to-batch mapping is deliberately kept out of the protocol repository -- section
    /// 7.2 says so, because rescheduling a batch must not become a protocol change that voids both
    /// ends' gate evidence -- so the boundary has to be stated somewhere on this side, and this is
    /// the smallest form it takes.
    /// <see cref="EveryPinnedVectorBelongsOnlyToSlicesThisBatchDoesNotImplement"/> is what makes
    /// the set load-bearing instead of decorative, and
    /// <see cref="TheIndexParsesIntoSixteenSlicesAndThirtyThreeDistinctVectors"/> is what lets it be
    /// stated as slice ids at all: it pins each slice's id to its own sequence, so the ids named
    /// here and the sequences the index carries cannot drift apart.
    /// </para>
    /// </remarks>
    private static readonly string[] SlicesThisLineImplements =
    [
        .. Enumerable.Range(0, 8).Select(sequence => FormattableString.Invariant($"FP-IS-{sequence:D2}")),
        "FP-IS-14",
        "FP-IS-15"
    ];

    /// <summary>
    /// The frozen vectors that have no named test, each with the slice that would prove it and either
    /// the batch that slice is scheduled into or the ticket that claims the vector. <b>Every entry is
    /// a vector whose slice nobody has built yet, or a vector a protocol upgrade added to a built
    /// slice and a named ticket has claimed; it is meant to empty as those land.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It held eleven when this test landed, and each of the eleven was checked rather than
    /// assumed: <c>DemandSelectionRequested</c>, <c>SlotConfigurationActivationCommand</c>,
    /// <c>OnboardAlarmSnapshot</c>, <c>UnableToChargeFieldConfirmationRequested</c> and
    /// <c>ManualStationClearanceConfirmationRequested</c> -- the wire messages those slices are
    /// defined in terms of -- appear nowhere in <c>src/</c> on 2026-09-08. There is no test to bind
    /// because there is no implementation to test.
    /// </para>
    /// <para>
    /// <b>Pinning is not waiving.</b> The comparison below is exact in both directions, so binding
    /// a vector without deleting its line here fails, and a vector quietly losing its last named
    /// test fails too. What a pin cannot do is hide: every entry names the slice and the batch, and
    /// <see cref="EveryPinnedVectorBelongsOnlyToSlicesThisBatchDoesNotImplement"/> refuses a pin on
    /// any vector belonging to a slice this batch does implement -- which is the only way this set
    /// could have become a place to park an inconvenient red.
    /// </para>
    /// <para>
    /// <b>The claimed kind exists since the <c>2.0.0</c> candidate.</b> It added
    /// <c>CV-LOAD-CANCELLATION-BEFORE-LOAD</c> and <c>CV-SUBLOT-REJECTED-AFTER-ENTRY</c> to
    /// <c>FP-IS-02</c>, a slice this line has built, and section 19.4 of the second edition of the
    /// scope specification assigns them there. The behaviour they prove belongs to
    /// <c>8005-agv-control-server#83</c> and <c>#82</c>, which land after the ticket that vendors the
    /// candidate (<c>#84</c>). Such a pin must name its slice and the claiming ticket in exactly the
    /// form <see cref="ClaimedPinLabel"/> gives, and
    /// <see cref="EveryPinnedVectorBelongsOnlyToSlicesThisBatchDoesNotImplement"/> refuses every other
    /// pin on a built slice.
    /// </para>
    /// <para>
    /// The batch labels follow section 7.2 of the second edition, which rescheduled
    /// <c>FP-IS-08</c> through <c>13</c>; the first edition's numbers are gone.
    /// </para>
    /// <para>
    /// The shape is borrowed from
    /// <see cref="ProtocolReasonCodeArchitectureTests"/>'s pinned deviation set, which held eleven
    /// of its own when it landed and is empty today. <b>Keep the field when it empties</b>: empty
    /// is itself the assertion, and a deviation with nowhere to go is how a gap survives eight
    /// green gates.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> VectorsAwaitingTheirSlice =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["CV-AUTOMATIC-CHARGING-CYCLE"] = "FP-IS-13, batch 9",
            ["CV-MANUAL-STATION-CLEARANCE"] = "FP-IS-13, batch 9",
            ["CV-MULTI-STOP-PLAN-NINE-LEGS"] = "FP-IS-08, batch 7",
            ["CV-REVERSED-DIRECTION-JOURNEY"] = "FP-IS-11, batch 6",
            ["CV-SUBLOT-REJECTED-AFTER-ENTRY"] = ClaimedPinLabel("FP-IS-02", 82),
            ["CV-TASK-TYPE-ADMISSION-FAIL-CLOSED"] = "FP-IS-10, batch 6",
            ["CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION"] = "FP-IS-13, batch 9",
            ["CV-WAITING-POINT-IDLE-RETURN"] = "FP-IS-12, batch 8",
            ["CV-WORKLIST-SELECTION-ACCEPTED"] = "FP-IS-09, batch 11",
            ["CV-WORKLIST-SELECTION-STALE-REVISION"] = "FP-IS-09, batch 11"
        };

    /// <summary>
    /// The only label a pin on a slice this line has built may carry: the slice and the ticket in
    /// this repository that claims the vector.
    /// </summary>
    private static string ClaimedPinLabel(string sliceId, int issueNumber) =>
        FormattableString.Invariant($"{sliceId}, claimed by 8005-agv-control-server#{issueNumber}");

    private sealed record Slice(string SliceId, int Sequence, IReadOnlyList<string> VectorIds);

    private sealed record VectorBinding(string VectorId, string TestName);

    [Fact]
    public void TheVendoredIndexIsTheProtocolIndexByteForByte()
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(IndexPath())))
            .ToLowerInvariant();

        Assert.Equal(ApprovedIndexSha256, actual);
    }

    /// <summary>
    /// The parse of the index, checked against the shape the <c>2.0.0</c> candidate froze.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion below could pass over an empty parse. The three counts are the
    /// ones that differ from each other -- 16 slices, 36 <c>vectorIds</c> entries, 33 distinct
    /// vectors -- so a parse that lost a slice, or one that forgot to deduplicate, is reported here
    /// rather than silently narrowing what the binding check covers. The three vectors shared
    /// across slices are counted rather than named: the count is the entire difference between 36
    /// and 33, and writing their ids out would put a hand-copied fragment of the vector list in a
    /// file whose whole point is not to hold one. v2 froze 34 and 31; the candidate added two
    /// vectors, both to <c>FP-IS-02</c> and neither shared.
    /// </remarks>
    [Fact]
    public void TheIndexParsesIntoSixteenSlicesAndThirtyThreeDistinctVectors()
    {
        Slice[] slices = Slices();
        string[] entries = [.. slices.SelectMany(slice => slice.VectorIds)];

        Assert.Equal(16, slices.Length);
        Assert.Equal(36, entries.Length);
        Assert.Equal(33, FrozenVectorIds().Length);

        // Each slice's id paired with its own sequence, not the two sets compared separately.
        // LastSliceSequenceThisBatchImplements is stated as a sequence and read as a batch
        // boundary on FP-IS-NN, which only holds while the two agree row by row.
        Assert.Equal(
            [.. Enumerable.Range(0, 16).Select(sequence => $"FP-IS-{sequence:D2}/{sequence}")],
            [
                .. slices.OrderBy(slice => slice.Sequence)
                    .Select(slice => $"{slice.SliceId}/{slice.Sequence}")
            ]);
        Assert.Equal(
            3,
            entries.GroupBy(vectorId => vectorId, StringComparer.Ordinal).Count(
                group => group.Count() > 1));
    }

    /// <summary>
    /// Every frozen vector either has a named test that runs, or is pinned to a slice nobody has
    /// built yet. The comparison is exact, so it fails in both directions.
    /// </summary>
    [Fact]
    public void EveryFrozenVectorIsBoundToANamedTestOrPinnedToASliceNobodyHasBuiltYet()
    {
        VectorBinding[] bindings = Bindings();
        Assert.NotEmpty(bindings);

        string[] withoutANamedTest =
            VectorsWithoutANamedTest(bindings.Select(binding => binding.VectorId));
        string[] unboundAndUnpinned = NewlyUnbound(VectorsAwaitingTheirSlice.Keys, withoutANamedTest);
        string[] pinnedInVain = PinnedInVain(VectorsAwaitingTheirSlice.Keys, withoutANamedTest);

        Assert.True(
            unboundAndUnpinned.Length == 0,
            "These frozen vectors have no named test and are not pinned to an unbuilt slice. "
            + "Either a test proving them was deleted, or its " + VectorTrait + " trait was: "
            + string.Join(", ", unboundAndUnpinned));

        Assert.True(
            pinnedInVain.Length == 0,
            "These vectors are pinned as having no named test, but they are not among the frozen "
            + "vectors that lack one -- either a test now binds them, in which case delete their "
            + "line from " + nameof(VectorsAwaitingTheirSlice) + ", or the pinned id is not a "
            + "frozen vector at all and is misspelled: " + string.Join(", ", pinnedInVain));
    }

    /// <summary>
    /// No test claims a <c>vectorId</c> the protocol never froze. This is the leg that catches a
    /// misspelling: a mistyped id binds nothing, and the vector it was meant to bind is reported by
    /// the check above as having lost its last named test.
    /// </summary>
    [Fact]
    public void NoTestClaimsAVectorIdTheProtocolNeverFroze()
    {
        VectorBinding[] bindings = Bindings();
        HashSet<string> claimed = new(
            ClaimedButNeverFrozen(bindings.Select(binding => binding.VectorId)),
            StringComparer.Ordinal);

        string[] offences =
        [
            .. bindings
                .Where(binding => claimed.Contains(binding.VectorId))
                .Select(binding => $"{binding.TestName} claims {binding.VectorId}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offences.Length == 0,
            "Tests carry a " + VectorTrait + " trait naming vectors the protocol did not freeze: "
            + string.Join("; ", offences));
    }

    /// <summary>
    /// Nothing is pinned that this batch is supposed to have built, unless a named ticket claims it.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="VectorsAwaitingTheirSlice"/> would be a way to turn any red green by
    /// adding a line. A vector belonging to a slice named in
    /// <see cref="SlicesThisLineImplements"/> is a vector this line really builds, and its absence
    /// from the suite is a gap rather than a schedule. The one exception is a vector a protocol
    /// upgrade added to such a slice: it may be pinned only with <see cref="ClaimedPinLabel"/> naming
    /// that very slice and the ticket that claims it, so the gap stays owned and visible.
    /// </remarks>
    [Fact]
    public void EveryPinnedVectorBelongsOnlyToSlicesThisBatchDoesNotImplement()
    {
        string[] wronglyPinned = WronglyPinned(VectorsAwaitingTheirSlice);

        Assert.True(
            wronglyPinned.Length == 0,
            "These vectors belong to slices this batch implements and their pin does not name the "
            + "claiming ticket in the form " + nameof(ClaimedPinLabel) + " gives, so a missing named "
            + "test is a gap rather than a schedule: " + string.Join(", ", wronglyPinned));
    }

    /// <summary>
    /// Proves the claimed-pin exception is narrow: the same vector pinned to a batch rather than a
    /// ticket, or to a ticket under a slice it does not belong to, is still refused.
    /// </summary>
    [Fact]
    public void AClaimedPinOnABuiltSliceMustNameThatSliceAndItsTicket()
    {
        const string vectorId = "CV-LOAD-CANCELLATION-BEFORE-LOAD";

        Assert.Empty(WronglyPinned(new Dictionary<string, string>
        {
            [vectorId] = ClaimedPinLabel("FP-IS-02", 83)
        }));
        Assert.Equal(
            [vectorId],
            WronglyPinned(new Dictionary<string, string> { [vectorId] = "FP-IS-02, batch 5" }));
        Assert.Equal(
            [vectorId],
            WronglyPinned(new Dictionary<string, string> { [vectorId] = ClaimedPinLabel("FP-IS-07", 83) }));
        Assert.Equal(
            [vectorId],
            WronglyPinned(new Dictionary<string, string>
            {
                [vectorId] = ClaimedPinLabel("FP-IS-02", 83) + " until later"
            }));
    }

    /// <summary>
    /// Pins on a vector of a built slice whose label is not exactly a claim naming one of that
    /// vector's built slices.
    /// </summary>
    private static string[] WronglyPinned(IReadOnlyDictionary<string, string> pinned)
    {
        Slice[] slices = Slices();

        return
        [
            .. pinned
                .Where(pin =>
                {
                    Slice[] builtOwners =
                    [
                        .. slices.Where(slice =>
                            SlicesThisLineImplements.Contains(slice.SliceId, StringComparer.Ordinal)
                            && slice.VectorIds.Contains(pin.Key, StringComparer.Ordinal))
                    ];

                    return builtOwners.Length > 0
                        && !builtOwners.Any(slice => ClaimedPin.IsMatch(pin.Value)
                            && pin.Value.StartsWith($"{slice.SliceId}, ", StringComparison.Ordinal));
                })
                .Select(pin => pin.Key)
                .Order(StringComparer.Ordinal)
        ];
    }

    private static readonly System.Text.RegularExpressions.Regex ClaimedPin = new(
        "^FP-IS-[0-9]{2}, claimed by 8005-agv-control-server#[1-9][0-9]*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Proves the binding check is not vacuous, by running it over a set that lost a binding the
    /// suite really has.
    /// </summary>
    /// <remarks>
    /// A real vector rather than a fabricated one: <c>CV-SESSION-RECOVERY-HAPPY</c> is bound by
    /// named tests today, so dropping it reproduces exactly what deleting those tests would do. The
    /// second assertion is the load-bearing half -- it says the vector is reported <i>because</i>
    /// nothing binds it, not because somebody had pinned it.
    /// </remarks>
    [Fact]
    public void TheBindingCheckCatchesAVectorThatLostItsLastNamedTest()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPY";
        string[] boundVectorIds =
        [
            .. Bindings().Select(binding => binding.VectorId)
                .Where(bound => !string.Equals(bound, vectorId, StringComparison.Ordinal))
        ];

        string[] withoutANamedTest = VectorsWithoutANamedTest(boundVectorIds);

        Assert.Contains(vectorId, withoutANamedTest);
        Assert.Contains(vectorId, NewlyUnbound(VectorsAwaitingTheirSlice.Keys, withoutANamedTest));
    }

    /// <summary>
    /// Proves the other direction is not vacuous: a pinned vector that a test does bind is
    /// reported, so the pin cannot outlive the gap it records.
    /// </summary>
    /// <remarks>
    /// The pinned set here is synthesised rather than read, because the vector being perturbed has
    /// to be one the suite really binds -- and by construction no such vector is in the real pin.
    /// It also means this proof survives the day batches 3 through 8 empty
    /// <see cref="VectorsAwaitingTheirSlice"/>, which is exactly when someone might be tempted to
    /// delete the comparison it guards.
    /// </remarks>
    [Fact]
    public void TheBindingCheckCatchesAPinnedVectorThatSomethingNowBinds()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPY";
        string[] boundVectorIds = [.. Bindings().Select(binding => binding.VectorId)];

        Assert.Contains(vectorId, boundVectorIds);
        Assert.Contains(
            vectorId,
            PinnedInVain([vectorId], VectorsWithoutANamedTest(boundVectorIds)));
    }

    /// <summary>
    /// Proves the misspelling check is not vacuous, with an id shaped exactly like a real one.
    /// </summary>
    [Fact]
    public void TheBindingCheckCatchesATestThatNamesAVectorNobodyFroze()
    {
        const string vectorId = "CV-SESSION-RECOVERY-HAPPPY";
        string[] boundVectorIds = [.. Bindings().Select(binding => binding.VectorId), vectorId];

        Assert.Contains(vectorId, ClaimedButNeverFrozen(boundVectorIds));
    }

    /// <summary>
    /// Frozen vectors nothing binds and nobody pinned -- the direction that catches a deleted test.
    /// </summary>
    /// <remarks>
    /// The pinned set is a parameter rather than a direct read of
    /// <see cref="VectorsAwaitingTheirSlice"/> so that the two vacuity proofs can feed it a
    /// perturbed one. That also means they keep working after the real set empties, which is where
    /// batches 3 through 8 are meant to take it -- a proof that needs a real pin to perturb would
    /// have had to skip itself on that day, and this suite has no skipped tests.
    /// </remarks>
    private static string[] NewlyUnbound(
        IEnumerable<string> pinned,
        IEnumerable<string> withoutANamedTest) =>
    [
        .. withoutANamedTest.Except(pinned, StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// Pinned vectors that are not in fact frozen vectors lacking a named test -- either something
    /// binds them after all, or the pinned id is not a frozen vector. The direction that keeps the
    /// pin from outliving the gap it records.
    /// </summary>
    private static string[] PinnedInVain(
        IEnumerable<string> pinned,
        IEnumerable<string> withoutANamedTest) =>
    [
        .. pinned.Except(withoutANamedTest, StringComparer.Ordinal).Order(StringComparer.Ordinal)
    ];

    private static string[] VectorsWithoutANamedTest(IEnumerable<string> boundVectorIds) =>
    [
        .. FrozenVectorIds()
            .Except(boundVectorIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static string[] ClaimedButNeverFrozen(IEnumerable<string> boundVectorIds) =>
    [
        .. boundVectorIds
            .Except(FrozenVectorIds(), StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    /// Every claim a test in this assembly makes on a frozen vector.
    /// </summary>
    /// <remarks>
    /// A binding only counts on a method that actually runs: a <c>[Fact(Skip = ...)]</c> carrying
    /// the trait would otherwise let a vector be marked proven by a test nobody executes, which is
    /// the mechanised form of the hand-counting this class replaces. Class-level traits are read as
    /// well as method-level ones, because xUnit applies them to every test in the class and a reader
    /// would reasonably expect them to bind.
    /// </remarks>
    private static VectorBinding[] Bindings() =>
    [
        .. typeof(ProtocolVectorTestBindingArchitectureTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(RunsAsATest)
            .SelectMany(method => method
                .GetCustomAttributes<TraitAttribute>()
                .Concat(method.DeclaringType!.GetCustomAttributes<TraitAttribute>())
                .Where(trait => string.Equals(trait.Name, VectorTrait, StringComparison.Ordinal))
                .Select(trait => new VectorBinding(
                    trait.Value,
                    $"{method.DeclaringType!.Name}.{method.Name}")))
    ];

    private static bool RunsAsATest(MethodInfo method) => method
        .GetCustomAttributes()
        .OfType<IFactAttribute>()
        .Any(fact => fact.Skip is null);

    private static string[] FrozenVectorIds() =>
    [
        .. Slices()
            .SelectMany(slice => slice.VectorIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static Slice[] Slices()
    {
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(IndexPath()));

        return
        [
            .. index.RootElement.GetProperty("slices").EnumerateArray().Select(slice => new Slice(
                slice.GetProperty("integrationSliceId").GetString()!,
                slice.GetProperty("sequence").GetInt32(),
                [
                    .. slice.GetProperty("vectorIds").EnumerateArray()
                        .Select(vectorId => vectorId.GetString()!)
                ]))
        ];
    }

    private static string IndexPath() => Path.Combine(
        RepositoryRoot(), "vendor", "8005-agv-protocol", "integration-slices", "index.json");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }
}
