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
