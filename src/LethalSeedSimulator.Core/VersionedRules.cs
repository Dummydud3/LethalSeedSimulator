using System.Text.Json;
using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public interface IVersionRuleProvider
{
    RulePack Load(string version);

    IReadOnlyList<string> ListVersions();
}

public sealed class JsonVersionRuleProvider(string rulesRootPath) : IVersionRuleProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public RulePack Load(string version)
    {
        var path = Path.Combine(rulesRootPath, version, "rulepack.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Rule pack not found for version '{version}'.", path);
        }

        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<RulePack>(json, JsonOptions);
        if (model is null)
        {
            throw new InvalidOperationException($"Failed to deserialize rule pack from '{path}'.");
        }

        return model;
    }

    public IReadOnlyList<string> ListVersions()
    {
        if (!Directory.Exists(rulesRootPath))
        {
            return [];
        }

        return Directory
            .GetDirectories(rulesRootPath)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
