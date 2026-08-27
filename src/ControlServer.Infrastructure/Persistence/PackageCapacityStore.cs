using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed class PackageCapacityStore(ControlServerDbContext dbContext) : IPackageCapacityStore
{
    public async Task<int?> ResolveAndTrackAsync(
        string package,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        PackageCapacityRuleRow[] rules = await dbContext.PackageCapacityRules
            .Where(row => row.SupersededAt == null)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        PackageCapacityRuleRow? match = rules.SingleOrDefault(row =>
            row.MatchType == "exact" && string.Equals(row.Pattern, package, StringComparison.Ordinal));
        match ??= rules
            .Where(row => row.MatchType == "prefix" && package.StartsWith(row.Pattern, StringComparison.Ordinal))
            .OrderByDescending(row => row.Pattern.Length)
            .FirstOrDefault();

        MissingPackageRow? missing = await dbContext.MissingPackages
            .SingleOrDefaultAsync(row => row.Package == package, cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            if (missing is null)
            {
                dbContext.MissingPackages.Add(new MissingPackageRow
                {
                    Package = package,
                    FirstSeenAt = observedAt,
                    LastSeenAt = observedAt,
                    Status = "PENDING"
                });
            }
            else
            {
                missing.LastSeenAt = observedAt;
                missing.Status = "PENDING";
            }
        }
        else if (missing is not null)
        {
            missing.LastSeenAt = observedAt;
            missing.Status = "SUPPLIED";
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return match?.MaxBoxesPerBasket;
    }
}

public static class PackageCapacitySeed
{
    private const string Source = "客户提供花篮容量对照表（2026-07-16迁移）";
    private static readonly DateTimeOffset EffectiveAt = new(2026, 7, 16, 0, 0, 0, TimeSpan.FromHours(8));

    public static readonly PackageCapacityRuleRow[] Rows =
    [
        Exact("PDFN5×6-8L(12R)", 4), Exact("PDFNWB3.3×3.34", 4), Exact("PDFNWB5×6", 4),
        Exact("TO-126", 8), Exact("TO-220-2L-C", 8), Exact("TO-220-3L", 8),
        Exact("TO-220-3L-C(T0.5mm)", 8), Exact("TO-220-5L", 8), Exact("TO-220D-5L", 8),
        Exact("TO-220F", 8), Exact("TO-247", 6), Exact("TO-247-2L-A", 6),
        Exact("TO-247-4L", 6), Exact("TO-247A-4L", 6), Exact("TO-247B-3L", 6),
        Exact("TO-247Plus-4L", 6), Exact("TO-251", 10), Exact("TO-252-2L(4R)", 5),
        Exact("TO-252-2L(6R)", 4), Exact("TO-252-2L(8R)", 4), Exact("TO-252-5L", 10),
        Exact("TO-263-2L", 8), Exact("TO-263-5L", 8), Exact("TO-263-7L(2R)", 6),
        Exact("TO-263C-2L", 8), Exact("TO-264-3L", 8), Exact("TO-92", 12),
        Prefix("TOLL-", 3)
    ];

    private static PackageCapacityRuleRow Exact(string pattern, int capacity) => Create(pattern, "exact", capacity);

    private static PackageCapacityRuleRow Prefix(string pattern, int capacity) => Create(pattern, "prefix", capacity);

    private static PackageCapacityRuleRow Create(string pattern, string matchType, int capacity) => new()
    {
        RuleId = $"v1:{matchType}:{pattern}",
        Pattern = pattern,
        MatchType = matchType,
        MaxBoxesPerBasket = capacity,
        Version = 1,
        Source = Source,
        EffectiveAt = EffectiveAt
    };
}
