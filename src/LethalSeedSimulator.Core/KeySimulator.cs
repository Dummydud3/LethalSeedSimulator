using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class KeySimulator
{
    public KeySpawnReport Simulate(LevelRule level, int runSeed, int levelRandomOffset)
    {
        var levelRandom = new Random(runSeed + levelRandomOffset);
        var dungeonFlowId = RollDungeonFlowId(level, levelRandom);
        var dungeonSeed = levelRandom.Next();
        var keyRandom = new Random(dungeonSeed + 914);

        var maxLocks = Math.Max(2, level.DungeonFlowTypes.Count + 2);
        var lockCount = keyRandom.Next(1, maxLocks + 1);
        var placements = new List<KeySpawnResult>(lockCount);
        for (var i = 0; i < lockCount; i++)
        {
            placements.Add(new KeySpawnResult
            {
                PlacementId = $"flow_{dungeonFlowId}_spawn_{i}"
            });
        }

        return new KeySpawnReport
        {
            KeyCount = lockCount,
            DungeonSeed = dungeonSeed,
            DungeonFlowId = dungeonFlowId,
            Placements = placements
        };
    }

    private static int RollDungeonFlowId(LevelRule level, Random levelRandom)
    {
        if (level.DungeonFlowTypes.Count == 0)
        {
            return 0;
        }

        var weights = level.DungeonFlowTypes.Select(x => x.Rarity).ToArray();
        var index = WeightedPicker.GetRandomWeightedIndex(weights, levelRandom);
        return level.DungeonFlowTypes[Math.Clamp(index, 0, level.DungeonFlowTypes.Count - 1)].Id;
    }
}
