using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace ControlServer.Tests;

public sealed class RiotSdkPackageProvenanceTests
{
    private const string PackageVersion = "0.1.0-controlserver.2";
    private const string RepositoryCommit = "e708f874fa3b76f9ed1cf39c2f97e4a026c13c10";
    private static readonly string[] PackageIds =
    [
        "RIoT.Sdk.Core",
        "RIoT.Sdk.Facade",
        "RIoT.Sdk.Generated"
    ];

    [Fact]
    public void ThreeRuntimePackagesMatchCommittedSha256Manifest()
    {
        string packageDirectory = FindPackageDirectory();
        Dictionary<string, string> expectedHashes = File.ReadAllLines(
                Path.Combine(packageDirectory, "SHA256SUMS"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        string[] packages = Directory.GetFiles(packageDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(GetFileName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(3, packages.Length);
        Assert.Equal(
            PackageIds.Select(id => $"{id}.{PackageVersion}.nupkg"),
            packages.Select(GetFileName));
        foreach (string package in packages)
        {
            string fileName = GetFileName(package);
            Assert.True(expectedHashes.TryGetValue(fileName, out string? expected),
                $"SHA256SUMS has no entry for {fileName}.");
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)))
                .ToLowerInvariant();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RuntimePackagesDeclareApprovedVersionAndRepositoryCommit()
    {
        string packageDirectory = FindPackageDirectory();
        foreach (string packageId in PackageIds)
        {
            XDocument nuspec = ReadNuspec(Path.Combine(
                packageDirectory,
                $"{packageId}.{PackageVersion}.nupkg"));
            XElement metadata = nuspec.Descendants().Single(element => element.Name.LocalName == "metadata");
            XElement repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");

            Assert.Equal(packageId, ElementValue(metadata, "id"));
            Assert.Equal(PackageVersion, ElementValue(metadata, "version"));
            Assert.Equal("git", repository.Attribute("type")?.Value);
            Assert.Equal("https://github.com/trytoreachpeak0/8005---AGV", repository.Attribute("url")?.Value);
            Assert.Equal(RepositoryCommit, repository.Attribute("commit")?.Value);
        }
    }

    [Fact]
    public void FacadePackageDependsOnCoreAndGeneratedAtTheSameImmutableVersion()
    {
        XDocument nuspec = ReadNuspec(Path.Combine(
            FindPackageDirectory(),
            $"RIoT.Sdk.Facade.{PackageVersion}.nupkg"));
        Dictionary<string, string?> dependencies = nuspec.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .ToDictionary(
                element => element.Attribute("id")!.Value,
                element => element.Attribute("version")?.Value,
                StringComparer.Ordinal);

        Assert.Equal(PackageVersion, dependencies["RIoT.Sdk.Core"]);
        Assert.Equal(PackageVersion, dependencies["RIoT.Sdk.Generated"]);
    }

    private static string FindPackageDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solution = Path.Combine(directory.FullName, "ControlServer.sln");
            string packages = Path.Combine(
                directory.FullName,
                "vendor",
                "nuget",
                "riot-sdk",
                PackageVersion);
            if (File.Exists(solution) && Directory.Exists(packages))
            {
                return packages;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ControlServer repository root and vendored RIoT SDK packages.");
    }

    private static XDocument ReadNuspec(string packagePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.Entries.Single(candidate =>
            candidate.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using Stream stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string ElementValue(XElement parent, string localName) =>
        parent.Elements().Single(element => element.Name.LocalName == localName).Value;

    private static string GetFileName(string path) =>
        Path.GetFileName(path) ?? throw new InvalidDataException($"Package path has no file name: {path}");
}
