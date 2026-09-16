using System.Text.Json;
using ControlServer.FakeOnboard;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// The synthetic peer's per-slot seed (control-server#71): what the two handshake snapshots report when an L2
/// setup file says <c>SlotStates</c>. Read from the same command-line shape Invoke-L2Scenario.ps1 passes.
/// </summary>
/// <remarks>
/// That the server then counts fewer available slots is shown end to end by the L2 run in the ticket's PR,
/// against the real server; these pin the seed's own contract -- the untouched slots keep exactly the shape the
/// peer always sent, and a typo fails the peer at startup instead of a handshake the server rejects.
/// </remarks>
public sealed class FakeOnboardSlotStateSeedTests
{
    [Fact]
    public void WithoutASeedEverySlotReportsTheWorkingVehicleDefault()
    {
        JsonElement[] slots = Render(SlotStateSeed.Read(Configuration()));

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], slots.Select(slot => slot.GetProperty("slotNo").GetInt32()));
        Assert.All(slots, slot =>
        {
            Assert.Equal("OPERABLE", slot.GetProperty("operability").GetString());
            Assert.Equal("ENABLED", slot.GetProperty("administrativeAvailability").GetString());
            Assert.Equal("EMPTY", slot.GetProperty("physicalState").GetString());
            Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
            Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());
            Assert.Equal(0, slot.GetProperty("reasonCodes").GetArrayLength());
        });
    }

    [Fact]
    public void ASeedChangesOnlyTheFieldsItNamesOnTheSlotsItNames()
    {
        JsonElement[] slots = Render(SlotStateSeed.Read(Configuration(
            "--FakeOnboard:Seed:slotStates:0:slotNo=1",
            "--FakeOnboard:Seed:slotStates:0:physicalState=OCCUPIED",
            "--FakeOnboard:Seed:slotStates:1:slotNo=6",
            "--FakeOnboard:Seed:slotStates:1:administrativeAvailability=DISABLED",
            "--FakeOnboard:Seed:slotStates:1:operability=INOPERABLE")));

        Assert.Equal("OCCUPIED", slots[0].GetProperty("physicalState").GetString());
        Assert.Equal("ENABLED", slots[0].GetProperty("administrativeAvailability").GetString());
        Assert.Equal("DISABLED", slots[5].GetProperty("administrativeAvailability").GetString());
        Assert.Equal("INOPERABLE", slots[5].GetProperty("operability").GetString());
        Assert.Equal("EMPTY", slots[5].GetProperty("physicalState").GetString());

        // The server counts a slot available only when it is OPERABLE, ENABLED, EMPTY, LOCKED and RESET
        // (JourneyRuntimeEngine.SlotAvailable); the six untouched slots still are, the two seeded ones are not.
        int[] available =
        [
            .. slots
                .Where(slot => slot.GetProperty("operability").GetString() == "OPERABLE"
                    && slot.GetProperty("administrativeAvailability").GetString() == "ENABLED"
                    && slot.GetProperty("physicalState").GetString() == "EMPTY"
                    && slot.GetProperty("lockState").GetString() == "LOCKED"
                    && slot.GetProperty("unlockOutputState").GetString() == "RESET")
                .Select(slot => slot.GetProperty("slotNo").GetInt32())
        ];
        Assert.Equal([2, 3, 4, 5, 7, 8], available);
    }

    [Theory]
    [InlineData("slotNo=0", "physicalState=OCCUPIED")]
    [InlineData("slotNo=9", "physicalState=OCCUPIED")]
    [InlineData("slotNo=one", "physicalState=OCCUPIED")]
    [InlineData("slotNo=3", "lockState=UNLOCKED")]
    [InlineData("slotNo=3", "physicalState=FULL")]
    [InlineData("slotNo=3", "administrativeAvailability=disabled")]
    public void AnEntryTheProtocolCannotCarryFailsAtStartup(string slot, string field)
    {
        IConfiguration configuration = Configuration(
            $"--FakeOnboard:Seed:slotStates:0:{slot}",
            $"--FakeOnboard:Seed:slotStates:0:{field}");

        Assert.Throws<InvalidOperationException>(() => SlotStateSeed.Read(configuration));
    }

    [Fact]
    public void NamingOneSlotTwiceFailsAtStartup()
    {
        IConfiguration configuration = Configuration(
            "--FakeOnboard:Seed:slotStates:0:slotNo=2",
            "--FakeOnboard:Seed:slotStates:0:physicalState=OCCUPIED",
            "--FakeOnboard:Seed:slotStates:1:slotNo=2",
            "--FakeOnboard:Seed:slotStates:1:administrativeAvailability=DISABLED");

        Assert.Throws<InvalidOperationException>(() => SlotStateSeed.Read(configuration));
    }

    private static IConfiguration Configuration(params string[] arguments) =>
        new ConfigurationBuilder().AddCommandLine(arguments).Build();

    private static JsonElement[] Render(SlotStateSeed seed) =>
        [.. JsonSerializer.SerializeToElement(seed.Render()).EnumerateArray()];
}
