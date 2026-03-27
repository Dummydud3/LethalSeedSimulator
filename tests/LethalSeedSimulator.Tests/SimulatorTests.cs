using LethalSeedSimulator.Core;
using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Tests;

public sealed class SimulatorTests
{
    [Fact]
    public void WeightedPicker_FallsBackToUniform_WhenWeightsAreNonPositive()
    {
        var random = new Random(42);
        var index = WeightedPicker.GetRandomWeightedIndex([0, -1, 0], random);
        Assert.InRange(index, 0, 2);
    }

    [Fact]
    public void Simulator_IsDeterministic_ForSameSeed()
    {
        var simulator = new SeedSimulator();
        var rules = DemoRules();

        var a = simulator.Simulate(rules, "bootstrap", 123456);
        var b = simulator.Simulate(rules, "bootstrap", 123456);

        Assert.Equal(a.TotalScrapValue, b.TotalScrapValue);
        Assert.Equal(a.ScrapCount, b.ScrapCount);
        Assert.Equal(a.Weather, b.Weather);
        Assert.Equal(
            a.ScrapRolls.Select(x => $"{x.ItemId}:{x.Value}"),
            b.ScrapRolls.Select(x => $"{x.ItemId}:{x.Value}"));
    }

    [Fact]
    public void Query_OnlyGoldbarDay_MatchesWhenAllItemsAreGoldbar()
    {
        var predicate = SeedQuery.Compile("only-goldbar-day");
        var report = new SeedReport
        {
            Version = "v",
            LevelId = "l",
            Seed = 1,
            Weather = "None",
            ScrapCount = 2,
            TotalScrapValue = 200,
            ScrapRolls =
            [
                new ScrapRollResult { ItemId = 152767, ItemName = "GoldBar", Value = 100 },
                new ScrapRollResult { ItemId = 152767, ItemName = "GoldBar", Value = 100 }
            ]
        };

        Assert.True(predicate(report));
    }

    private static RulePack DemoRules() =>
        new()
        {
            Metadata = new RulePackMetadata
            {
                RuleSetVersion = "demo",
                GameVersionLabel = "demo",
                SourceSignature = "test",
                ExtractedAtUtc = DateTimeOffset.UtcNow
            },
            Offsets = new RngOffsets(),
            Levels =
            [
                new LevelRule
                {
                    Id = "bootstrap",
                    Name = "Bootstrap",
                    MinScrap = 2,
                    MaxScrapExclusive = 5,
                    SpawnableScrap =
                    [
                        new ScrapRule { ItemId = 152767, ItemName = "GoldBar", Rarity = 10, MinValueInclusive = 50, MaxValueExclusive = 80, TwoHanded = true },
                        new ScrapRule { ItemId = 300, ItemName = "Mug", Rarity = 30, MinValueInclusive = 20, MaxValueExclusive = 50, TwoHanded = false }
                    ],
                    Weathers = [new WeatherRule { WeatherType = "None", Weight = 1 }]
                }
            ]
        };
}
