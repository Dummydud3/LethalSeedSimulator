using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class KeySimulator
{
    public KeySpawnReport Simulate(
        LevelRule level,
        GlobalRules globalRules,
        int rolledDungeonFlowId,
        int runSeed,
        int levelRandomOffset)
    {
        var levelRandom = new Random(runSeed + levelRandomOffset);
        var dungeonFlowId = rolledDungeonFlowId;
        ConsumeDungeonFlowRoll(level, levelRandom);
        var dungeonSeed = levelRandom.Next();
        var flow = globalRules.DungeonFlows.FirstOrDefault(x => x.Id == dungeonFlowId);
        var lockBaseline = flow is not null && flow.EstimatedLockableDoorCount > 0
            ? flow.EstimatedLockableDoorCount
            : globalRules.EstimatedLockableDoorCount;
        var lockCount = RollLockedDoorCount(lockBaseline, levelRandom);
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
            DungeonFlowName = flow?.Name ?? $"Flow{dungeonFlowId}",
            DungeonFlowTheme = flow?.Theme ?? "Unknown",
            Placements = placements
        };
    }

    private static int RollLockedDoorCount(int estimatedLockableDoorCount, Random levelRandom)
    {
        if (estimatedLockableDoorCount <= 0)
        {
            return 0;
        }

        var chance = 1.1;
        var locked = 0;
        for (var i = 0; i < estimatedLockableDoorCount; i++)
        {
            if (levelRandom.NextDouble() < chance)
            {
                locked++;
            }

            chance /= 1.55;
        }

        return locked;
    }

    private static void ConsumeDungeonFlowRoll(LevelRule level, Random levelRandom)
    {
        if (level.DungeonFlowTypes.Count == 0)
        {
            return;
        }

        var weights = level.DungeonFlowTypes.Select(x => x.Rarity).ToArray();
        WeightedPicker.GetRandomWeightedIndex(weights, levelRandom);
    }
}
