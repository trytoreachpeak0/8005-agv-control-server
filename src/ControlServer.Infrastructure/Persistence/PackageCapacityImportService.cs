using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed record PackageCapacityImportRule(
    string Pattern,
    string MatchType,
    int MaxBoxesPerBasket,
    string Source);

public sealed record PackageCapacityImportResult(int Inserted, int Unchanged, int Superseded);

public sealed class PackageCapacityImportService(ControlServerDbContext dbContext)
{
    public async Task<PackageCapacityImportResult> ImportAsync(
        IReadOnlyList<PackageCapacityImportRule> rules,
        int version,
        DateTimeOffset effectiveAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (rules.Count == 0) throw new InvalidDataException("Capacity import must contain at least one rule.");

        foreach (PackageCapacityImportRule rule in rules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Pattern);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Source);
            if (rule.MatchType is not ("exact" or "prefix"))
                throw new InvalidDataException("Capacity match_type must be exact or prefix.");
            if (rule.MaxBoxesPerBasket <= 0)
                throw new InvalidDataException("Capacity must be a positive integer.");
        }
        if (rules.Select(rule => (rule.Pattern, rule.MatchType)).Distinct().Count() != rules.Count)
            throw new InvalidDataException("Capacity import contains duplicate rule identities.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        int inserted = 0;
        int unchanged = 0;
        int superseded = 0;
        foreach (PackageCapacityImportRule rule in rules)
        {
            PackageCapacityRuleRow? active = await dbContext.PackageCapacityRules.SingleOrDefaultAsync(
                row => row.Pattern == rule.Pattern && row.MatchType == rule.MatchType && row.SupersededAt == null,
                cancellationToken).ConfigureAwait(false);
            if (active is not null && active.MaxBoxesPerBasket == rule.MaxBoxesPerBasket &&
                string.Equals(active.Source, rule.Source, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }
            if (active is not null)
            {
                if (version <= active.Version)
                    throw new InvalidOperationException(
                        $"Rule {rule.Pattern}/{rule.MatchType} requires a version greater than {active.Version}.");
                active.SupersededAt = effectiveAt;
                superseded++;
            }

            string ruleId = $"v{version}:{rule.MatchType}:{rule.Pattern}";
            if (await dbContext.PackageCapacityRules.AnyAsync(row => row.RuleId == ruleId, cancellationToken)
                .ConfigureAwait(false))
                throw new InvalidOperationException($"Rule version identity already exists: {ruleId}.");
            dbContext.PackageCapacityRules.Add(new PackageCapacityRuleRow
            {
                RuleId = ruleId,
                Pattern = rule.Pattern,
                MatchType = rule.MatchType,
                MaxBoxesPerBasket = rule.MaxBoxesPerBasket,
                Version = version,
                Source = rule.Source,
                EffectiveAt = effectiveAt
            });
            inserted++;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PackageCapacityImportResult(inserted, unchanged, superseded);
    }
}
