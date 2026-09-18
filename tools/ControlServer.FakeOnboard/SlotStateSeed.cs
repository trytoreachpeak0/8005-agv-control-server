using System.Globalization;

namespace ControlServer.FakeOnboard;

/// <summary>
/// The per-slot states this peer reports in its handshake snapshots, read once from
/// <c>FakeOnboard:Seed:slotStates:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry names a <c>slotNo</c> and any of <c>physicalState</c>, <c>administrativeAvailability</c> and
/// <c>operability</c>; a slot no entry names keeps the default a working vehicle reports (OPERABLE, ENABLED,
/// EMPTY, LOCKED, RESET). This is how an L2 scenario builds REQ-0352's "short because a slot is occupied or
/// disabled" without an IO rig (control-server#71).
/// </para>
/// <para>
/// Handshake only. The server reads slot availability off the session's CapabilitySnapshot and
/// SafetyStateSnapshot, which are sent once per session, so a runtime change would say nothing until the next
/// session anyway. A scenario that needs a different state starts the peer with a different seed.
/// </para>
/// <para>
/// Values are checked against the protocol's enums here rather than passed through: an unknown value would
/// fail schema validation on the server and surface as a handshake rejection, which reads like a protocol
/// fault rather than a typo in a setup file.
/// </para>
/// </remarks>
public sealed class SlotStateSeed
{
    public const string SectionName = "FakeOnboard:Seed:slotStates";
    public const int SlotCount = 8;

    private static readonly Dictionary<string, string[]> AllowedValues = new(StringComparer.Ordinal)
    {
        ["operability"] = ["OPERABLE", "INOPERABLE", "UNKNOWN"],
        ["administrativeAvailability"] = ["ENABLED", "DISABLE_PENDING", "DISABLED"],
        ["physicalState"] = ["EMPTY", "OCCUPIED", "UNKNOWN"]
    };

    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> overrides;

    private SlotStateSeed(IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> overrides)
    {
        this.overrides = overrides;
    }

    /// <summary>Reads the section; throws on an entry that names no valid slot, an unknown field or value.</summary>
    public static SlotStateSeed Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<int, IReadOnlyDictionary<string, string>> overrides = [];
        foreach (IConfigurationSection entry in configuration.GetSection(SectionName).GetChildren())
        {
            string? slotText = entry["slotNo"];
            if (!int.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out int slotNo) ||
                slotNo is < 1 or > SlotCount)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{entry.Key} needs a slotNo between 1 and {SlotCount}, not '{slotText}'.");
            }
            if (overrides.ContainsKey(slotNo))
            {
                throw new InvalidOperationException($"{SectionName} names slot {slotNo} twice.");
            }

            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            foreach (IConfigurationSection field in entry.GetChildren())
            {
                if (string.Equals(field.Key, "slotNo", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!AllowedValues.TryGetValue(field.Key, out string[]? allowed))
                {
                    throw new InvalidOperationException(
                        $"{SectionName}:{entry.Key}:{field.Key} is not a seedable slot state field; "
                        + $"use {string.Join(", ", AllowedValues.Keys)}.");
                }
                if (!allowed.Contains(field.Value, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{SectionName}:{entry.Key}:{field.Key} must be one of {string.Join(", ", allowed)}, "
                        + $"not '{field.Value}'.");
                }
                fields[field.Key] = field.Value!;
            }
            overrides[slotNo] = fields;
        }
        return new SlotStateSeed(overrides);
    }

    /// <summary>The eight slots in the shape protocol v2's <c>SlotState</c> carries, slot 1 first.</summary>
    public object[] Render() => Enumerable.Range(1, SlotCount)
        .Select(slotNo =>
        {
            IReadOnlyDictionary<string, string>? fields = overrides.GetValueOrDefault(slotNo);
            return (object)new
            {
                slotNo,
                operability = Field(fields, "operability", "OPERABLE"),
                administrativeAvailability = Field(fields, "administrativeAvailability", "ENABLED"),
                physicalState = Field(fields, "physicalState", "EMPTY"),
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            };
        })
        .ToArray();

    private static string Field(IReadOnlyDictionary<string, string>? fields, string name, string fallback) =>
        fields is not null && fields.TryGetValue(name, out string? value) ? value : fallback;
}
