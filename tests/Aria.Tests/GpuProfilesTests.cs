using Aria.Core.Nvidia;

namespace Aria.Tests;

public class GpuProfilesTests
{
    private static readonly GpuIdentity Tower = new("NVIDIA GeForce RTX 4080 SUPER", "GPU-e7d7e284-26d8-3c90-4d3b-abab815041f9", "621.01");
    private static readonly GpuIdentity Laptop = new("NVIDIA GeForce RTX 3060 Laptop GPU", "GPU-11111111-2222-3333-4444-555555555555", "621.01");

    private static SavedGpuProfile Profile(string name, GpuIdentity gpu, string state = "p=320000;c=2685;o=145;m=750;b=0", int day = 30) =>
        new(name, state, new DateTime(2026, 10, day, 14, 37, 0), gpu.Uuid, gpu.Name, gpu.DriverVersion, "Assistant Équilibre");

    [Fact]
    public void SavingUnderAnExistingNameReplacesThatProfileOnly()
    {
        var profiles = new[] { Profile("Équilibre du 30/10", Tower), Profile("Équilibre du 30/10", Laptop), Profile("Silence", Tower) };

        var updated = GpuProfiles.Upsert(profiles, Profile(" équilibre du 30/10 ", Tower, "p=320000;c=2685;o=130;m=500;b=0"), Tower);

        Assert.Equal(3, updated.Count);
        Assert.Equal(130, Assert.Single(updated, p => p.Matches(Tower) && p.Name.Trim() != "Silence").Tuning!.CoreOffsetMHz);
        Assert.Single(updated, p => p.Matches(Laptop)); // même nom sur l'autre carte : intact
    }

    [Fact]
    public void DeletingTouchesOnlyThisCard()
    {
        var profiles = new[] { Profile("Jeu", Tower), Profile("Jeu", Laptop) };

        var left = Assert.Single(GpuProfiles.Remove(profiles, "Jeu", Tower));

        Assert.True(left.Matches(Laptop));
    }

    [Fact]
    public void ProfilesSurviveTheFileAndKeepTheirTuning()
    {
        var json = GpuProfiles.ToJson([Profile("Équilibre du 30/10", Tower)]);

        var back = Assert.Single(GpuProfiles.FromJson(json));

        Assert.Equal("Équilibre du 30/10", back.Name);
        Assert.Equal(new GpuTuningState(320000, 2685, 145, 750, false), back.Tuning);
        Assert.Equal("30/10/2026 · pilote 621.01 · Assistant Équilibre", back.Details);
    }

    [Fact]
    public void ANewDriverIsFlagged()
    {
        var profile = Profile("Jeu", Tower);

        Assert.False(profile.DriverChanged(Tower));
        Assert.True(profile.DriverChanged(Tower with { DriverVersion = "625.10" }));
        Assert.Equal("Équilibre du 30/10", GpuProfiles.DefaultName("Équilibre", new DateTime(2026, 10, 30)));
    }
}
