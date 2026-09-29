namespace ControlServer.Application;

/// <summary>
/// The act mission a charging order carries after its move to the charger: action 78 with
/// actionParam1 1 starts charging (program allowlist section 1.2, shape two). One definition, read by
/// both the RIoT create call and the request-semantic digest, so the two cannot drift apart.
/// </summary>
/// <remarks>
/// Leaving the charger (<c>act(78, 2, 0)</c>) is never sent by this server: RIoT inserts it at the head of
/// the vehicle's next order.
/// </remarks>
public static class RiotChargingOrderAction
{
    public const int ActionId = 78;
    public const int StartChargingParam1 = 1;
    public const int Param2 = 0;
}
