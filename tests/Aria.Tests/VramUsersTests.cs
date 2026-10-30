using Aria.Core.Performance;

namespace Aria.Tests;

public class VramUsersTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>Compteur « GPU Process Memory » relevé sur la tour le 30/10, LLM d'Aria-Pilote chargé.</summary>
    private static readonly (string, long)[] Tower =
    [
        ("pid_12004_luid_0x00000000_0x00009ece_phys_0", 10520 * MiB),
        ("pid_1940_luid_0x00000000_0x00009ece_phys_0", 3282 * MiB),
        ("pid_21376_luid_0x00000000_0x00009ece_phys_0", 565 * MiB),
        ("pid_21052_luid_0x00000000_0x00009ece_phys_0", 219 * MiB),
        ("_Total", 14586 * MiB)
    ];

    private static string? Names(int pid) => pid switch
    {
        12004 => "llama-server",
        1940 => "dwm",
        21376 => "wallpaper64",
        21052 => "explorer",
        777 => "Aria-Bench",
        _ => null
    };

    [Fact]
    public void ALoadedLlmIsFoundAndTheSystemIsIgnored()
    {
        var users = VramUsers.Heavy(Tower, Names, self: 4242);

        var llm = Assert.Single(users);
        Assert.Equal("llama-server", llm.Name);
        Assert.Equal("llama-server (10,3 Go)", VramUsers.Describe(users));
    }

    [Fact]
    public void InstancesOfOneProcessAreAddedUp()
    {
        // Un même programme sur deux cartes (ou deux segments) : 600 + 600 Mo dépassent le seuil ensemble
        var users = VramUsers.Heavy(
            [("pid_12_luid_0x00000000_0x00009ece_phys_0", 600 * MiB), ("pid_12_luid_0x00000000_0x0000a1b2_phys_0", 600 * MiB)],
            _ => "jeu", self: 1);

        Assert.Equal(1200 * MiB, Assert.Single(users).Bytes);
    }

    [Fact]
    public void AriaItselfAndClosedProgramsDoNotCount()
    {
        var samples = new[]
        {
            ("pid_4242_luid_0x00000000_0x00009ece_phys_0", 2048 * MiB),  // Aria Cockpit (ce processus)
            ("pid_777_luid_0x00000000_0x00009ece_phys_0", 9000 * MiB),   // Aria-Bench en plein test
            ("pid_31337_luid_0x00000000_0x00009ece_phys_0", 4096 * MiB)  // fermé entre le relevé et la lecture du nom
        };

        Assert.Empty(VramUsers.Heavy(samples, Names, self: 4242));
    }
}
