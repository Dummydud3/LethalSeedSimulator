using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Extractor;

public interface IRuleExtractorAdapter
{
    string VersionKey { get; }

    RulePack Extract(string sourceRootPath);
}

public sealed class RuleExtractorRegistry(IReadOnlyList<IRuleExtractorAdapter> adapters)
{
    public IRuleExtractorAdapter Resolve(string versionKey)
    {
        return adapters.FirstOrDefault(x => string.Equals(x.VersionKey, versionKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No extractor adapter registered for '{versionKey}'. Available: {string.Join(", ", adapters.Select(x => x.VersionKey))}");
    }

    public IReadOnlyList<string> ListVersions() => adapters.Select(x => x.VersionKey).OrderBy(x => x).ToList();
}
