using Microsoft.VisualBasic.FileIO;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime;

public static class PackageCapacityImportCommand
{
    public static bool IsRequested(string[] args) =>
        args.Contains("--import-package-capacity", StringComparer.Ordinal);

    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        string input = RequiredArgument(args, "--input");
        int version = int.Parse(RequiredArgument(args, "--version"), System.Globalization.CultureInfo.InvariantCulture);
        List<PackageCapacityImportRule> rules = ReadCsv(input);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ControlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlServerDbContext>();
        PackageCapacityImportResult result = await new PackageCapacityImportService(dbContext).ImportAsync(
            rules, version, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Package capacity import complete: inserted={result.Inserted}, unchanged={result.Unchanged}, superseded={result.Superseded}.");
        return 0;
    }

    private static List<PackageCapacityImportRule> ReadCsv(string path)
    {
        using TextFieldParser parser = new(path, System.Text.Encoding.UTF8);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        string[] header = parser.ReadFields() ?? throw new InvalidDataException("Capacity CSV is empty.");
        string[] expected = ["pattern", "match_type", "max_boxes_per_basket", "source", "status", "note"];
        if (!header.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Capacity CSV header differs from the controlled six-column format.");

        List<PackageCapacityImportRule> rules = [];
        while (!parser.EndOfData)
        {
            string[] fields = parser.ReadFields() ?? throw new InvalidDataException("Capacity CSV contains an empty row.");
            if (fields.Length != expected.Length || fields.Any(field => field != field.Trim()))
                throw new InvalidDataException("Capacity CSV row is malformed or contains outer whitespace.");
            if (!string.Equals(fields[4], "active", StringComparison.Ordinal))
                throw new InvalidDataException("Capacity CSV status must be active.");
            rules.Add(new PackageCapacityImportRule(
                fields[0],
                fields[1],
                int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                fields[3]));
        }
        return rules;
    }

    private static string RequiredArgument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index == args.Length - 1 || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Required argument is missing: {name}.");
        return args[index + 1];
    }
}
