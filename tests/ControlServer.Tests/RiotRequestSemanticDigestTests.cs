using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// control-server#401: the request-semantic digest recorded on create audit rows. A single-move intent's
/// digest must not move, because audit rows already written carry it.
/// </summary>
public sealed class RiotRequestSemanticDigestTests
{
    // Pinned before order shapes existed, by RiotDispatchAuditTests
    // .CreateStartIsDurableBeforeGatewayMutationAndUnknownResponseIsSanitized, for exactly this intent.
    private const string SingleMoveDigestBeforeOrderShapes =
        "0b494b2bdbbd3dae5d8f4d4c1de6d508f18205b44bedaf874c99420c69483cfc";

    // The charging digest of the same intent, pinned so a change to how the act mission enters the digest goes red
    // (control-server#401 review). It is the SHA-256 of this UTF-8 JSON, computed independently of the implementation
    // (Python hashlib; the same method reproduces SingleMoveDigestBeforeOrderShapes above byte for byte):
    // {"schemaVersion":1,"endpoint":"byDefaultMissions","orderShape":"CHARGE","upperId":"UPPER-001",
    //  "appointVehicleKey":"AGV-8005-01","isAppointEnable":1,"lockStatus":0,"orderName":"UPPER-001",
    //  "missions":[{"type":"move","mapId":25,"destination":12},{"type":"act","actionId":78,"actionParam1":1,"actionParam2":0}]}
    // Changing it means every CHARGE create audit already written stops comparing equal to its next attempt.
    private const string ChargeDigest =
        "ecd85e02e21951aad3cb80128e713f40de46c4a69d37994508155a6fbaf163f6";

    [Fact]
    public void ASingleMoveIntentKeepsTheDigestItHadBeforeOrderShapes()
    {
        Assert.Equal(
            SingleMoveDigestBeforeOrderShapes,
            MovementDispatchService.ComputeRequestSemanticSha256(Intent(OrderShapes.SingleMove)));
    }

    [Fact]
    public void AnIntentWithoutAnExplicitShapeIsASingleMove()
    {
        OrderIntent intent = new(
            "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12",
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            "AGV-8005-01", 25, 12, 1, 1);

        Assert.Equal(SingleMoveDigestBeforeOrderShapes, MovementDispatchService.ComputeRequestSemanticSha256(intent));
    }

    [Fact]
    public void AChargeIntentDigestDiffersFromTheSingleMoveToTheSameStation()
    {
        string charge = MovementDispatchService.ComputeRequestSemanticSha256(Intent(OrderShapes.Charge));

        Assert.Matches("^[0-9a-f]{64}$", charge);
        Assert.Equal(ChargeDigest, charge);
        Assert.NotEqual(SingleMoveDigestBeforeOrderShapes, charge);
        Assert.Equal(charge, MovementDispatchService.ComputeRequestSemanticSha256(Intent(OrderShapes.Charge)));
    }

    [Fact]
    public void AnUnknownShapeNeverSharesADigestWithAKnownShape()
    {
        string unknown = MovementDispatchService.ComputeRequestSemanticSha256(Intent("MOVE"));

        Assert.NotEqual(SingleMoveDigestBeforeOrderShapes, unknown);
        Assert.NotEqual(MovementDispatchService.ComputeRequestSemanticSha256(Intent(OrderShapes.Charge)), unknown);
    }

    private static OrderIntent Intent(string orderShape) => new(
        "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12",
        new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
        "AGV-8005-01", 25, 12, 1, 1, orderShape);
}
