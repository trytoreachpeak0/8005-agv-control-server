using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvus.Json;
using Corvus.Json.CodeGeneration;
using Corvus.Json.CodeGeneration.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyModel;

namespace ControlServer.SchemaConformance;

/// <summary>
/// One compiled JSON Schema: what <c>Corvus.Json.Validator.JsonSchema</c> is to its callers, with the
/// same <see cref="Validate"/>.
/// </summary>
public sealed class CompiledSchema
{
    private readonly Func<JsonElement, ValidationLevel, ValidationContext> validate;

    internal CompiledSchema(Func<JsonElement, ValidationLevel, ValidationContext> validate) => this.validate = validate;

    public ValidationContext Validate(JsonElement element, ValidationLevel level = ValidationLevel.Flag) => validate(element, level);
}

/// <summary>
/// What a cache entry is compiled from, other than the schema's own <c>$id</c>. Every part is in the key: an entry
/// written under one value of any of them is never read under another (control-server#584).
/// </summary>
/// <param name="SchemaBundleSha256">
/// The digest of the schemas/ tree the schema is resolved against, measured from the files, never the constant the
/// tree is expected to hash to. A message schema <c>$ref</c>s the envelope and the shared definitions, so one changed
/// file anywhere in the tree can change what any schema compiles to: the key is the whole tree's digest, not the
/// file's.
/// </param>
/// <param name="ToolVersion">
/// The SHA-256 of the assembly that holds this class. Everything that decides how a schema is compiled -- the
/// options, the vocabularies, the code ported from Corvus below, the format of an entry -- is code in that assembly.
/// </param>
/// <param name="CorvusVersion">
/// Every Corvus and Roslyn package this process runs with, name and version: the code generator, the runtime types the
/// generated code is built against, and the compiler that builds it.
/// </param>
public sealed record SchemaCacheKeyParts(string SchemaBundleSha256, string ToolVersion, string CorvusVersion)
{
    /// <summary>
    /// The parts for this process, over a schemas/ tree whose digest <see cref="CompiledSchemaCache.SchemaBundleSha256"/>
    /// measured as <paramref name="measuredSchemaBundleSha256"/>.
    /// </summary>
    public static SchemaCacheKeyParts ForThisProcess(string measuredSchemaBundleSha256) => new(
        measuredSchemaBundleSha256,
        Sha256Hex(File.ReadAllBytes(typeof(CompiledSchemaCache).Assembly.Location)),
        CorvusPackages());

    private static string CorvusPackages()
    {
        DependencyContext context = DependencyContext.Default
            ?? throw new InvalidOperationException("No dependency context: the Corvus version cannot be read.");
        string[] packages = [.. context.RuntimeLibraries
            .Where(library => library.Name.StartsWith("Corvus.", StringComparison.OrdinalIgnoreCase) ||
                library.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            .Select(library => library.Name + "/" + library.Version)
            .Order(StringComparer.Ordinal)];
        return packages.Any(package => package.StartsWith("Corvus.Json.Validator/", StringComparison.OrdinalIgnoreCase))
            ? string.Join(";", packages)
            : throw new InvalidOperationException("Corvus.Json.Validator is not among this process's libraries.");
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>
/// Compiles JSON Schemas the way <c>Corvus.Json.Validator.JsonSchema.FromText</c> does, and keeps the compiled assembly
/// on disk so the next process that needs the same schema loads it instead (control-server#584).
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: compiling is most of what a run of the validator costs, 1-5 s a schema, and every G2 slice is a new
/// process that compiled the same schemas again. Corvus caches compiled schemas only inside one process, and keys that
/// cache on the schema's URI alone -- not its content -- so it is not a key to copy: a key without the content is how a
/// gate ends up judging new traffic by an old schema.
/// </para>
/// <para>
/// Corvus 4.6.7 has no public way to get at the assembly it compiles, so the steps are ported from its
/// <c>Solutions/Corvus.Json.Validator/JsonSchema.cs</c> at tag 4.6.7 (commit 67bdf391): <c>FromCore</c> (vocabularies,
/// type builder, code generation), the <c>NET8_0_OR_GREATER</c> <c>CompileGeneratedType</c> (Roslyn, with
/// <c>BuildMetadataReferencesAndDefines</c> and <c>ParseSyntaxTrees</c>), and <c>BuildValidateCallback</c>. What
/// differs is only where the assembly goes: emitted to bytes that are written to the cache, then loaded from those
/// bytes. The metaschema resolver is Corvus's own instance, read by reflection rather than copied with its embedded
/// resources. The options are the ones the validator always passed: the vendored tree as the additional resolver, no
/// file system or HTTP resolution, the 2020-12 fallback vocabulary, <c>format</c> always asserted.
/// </para>
/// <para>
/// An entry is one file, <c>&lt;key&gt;.schema-cache</c>: a JSON header line that repeats the key, names the root type
/// and carries the length and SHA-256 of the assembly, then the assembly. A file whose header or assembly does not check
/// out -- truncated, written by something else -- is compiled again and replaced, never trusted. Entries are written to
/// a file of their own and renamed into place, so a reader sees a whole entry or none, and two processes writing the
/// same key leave one whole entry. Assemblies are loaded from bytes, never from the file, so no process holds an entry
/// open and a replacement is never blocked by a reader.
/// </para>
/// </remarks>
public sealed class CompiledSchemaCache
{
    private const int FormatVersion = 1;
    private const string Extension = ".schema-cache";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const string GlobalUsingStatements =
        """
        // <auto-generated/>

        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        """;

    private readonly string? directory;
    private readonly SchemaCacheKeyParts keyParts;
    private readonly IDocumentResolver schemas;
    // One load per schema per process: the generated assembly's name is fixed per schema, and a load context refuses a
    // second assembly of the same name.
    private readonly Dictionary<string, CompiledSchema> loaded = new(StringComparer.Ordinal);
    private readonly AssemblyLoadContext loadContext = new("ControlServer.SchemaConformance.CompiledSchemas", isCollectible: false);
    private bool pruned;

    /// <param name="directory">Where entries live; null compiles every schema and keeps nothing.</param>
    /// <param name="keyParts">Everything besides the schema's <c>$id</c> that an entry is keyed on.</param>
    /// <param name="schemas">
    /// The vendored schemas, each registered under its own <c>$id</c>: the tree <see cref="SchemaCacheKeyParts.SchemaBundleSha256"/> was
    /// measured over.
    /// </param>
    public CompiledSchemaCache(string? directory, SchemaCacheKeyParts keyParts, IDocumentResolver schemas)
    {
        this.directory = directory;
        this.keyParts = keyParts;
        this.schemas = schemas;
    }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    /// <summary>Entries that were there but did not check out, and were compiled again.</summary>
    public int Rejected { get; private set; }

    public SchemaCacheKeyParts KeyParts => keyParts;

    /// <summary>The key of the entry for the schema registered under <paramref name="schemaId"/>.</summary>
    public string KeyOf(string schemaId) => SchemaCacheKeyParts.Sha256Hex(Encoding.UTF8.GetBytes(
        $"format={FormatVersion}\nschemaBundleSha256={keyParts.SchemaBundleSha256}\ntool={keyParts.ToolVersion}\n" +
        $"corvus={keyParts.CorvusVersion}\nschemaId={schemaId}\n"));

    /// <summary>The schema registered under <paramref name="schemaId"/>, from the cache when an entry checks out.</summary>
    public CompiledSchema Get(string schemaId)
    {
        string key = KeyOf(schemaId);
        if (loaded.TryGetValue(key, out CompiledSchema? already))
        {
            return already;
        }
        CompiledSchema schema = GetUncached(key, schemaId);
        loaded[key] = schema;
        return schema;
    }

    private CompiledSchema GetUncached(string key, string schemaId)
    {
        string? path = directory is null ? null : Path.Combine(directory, key + Extension);
        if (path is not null && File.Exists(path))
        {
            if (TryRead(path, key, schemaId) is { } entry)
            {
                Hits++;
                return Load(entry.Assembly, entry.RootTypeName);
            }
            Rejected++;
        }
        Misses++;
        (byte[] assembly, string rootTypeName) = Compile(schemaId);
        if (path is not null)
        {
            Write(path, key, schemaId, assembly, rootTypeName);
        }
        return Load(assembly, rootTypeName);
    }

    /// <summary>
    /// The protocol's own schema bundle digest, reproduced from schemas/: sorted relative paths, each written as
    /// "&lt;path&gt;:&lt;sha256&gt;\n", the whole hashed. Implemented in 8005-agv-protocol tools/finalize-manifest.mjs as
    /// combine("schemas/"), and the value it produces is the manifest's schemaBundleSha256, which ProtocolCandidateIdentity
    /// carries.
    /// </summary>
    public static string SchemaBundleSha256(string schemaRoot)
    {
        StringBuilder lines = new();
        foreach (string file in Directory.EnumerateFiles(schemaRoot, "*", SearchOption.AllDirectories)
            .OrderBy(file => RelativeUnixPath(schemaRoot, file), StringComparer.Ordinal))
        {
            lines.Append("schemas/").Append(RelativeUnixPath(schemaRoot, file)).Append(':')
                .Append(SchemaCacheKeyParts.Sha256Hex(File.ReadAllBytes(file))).Append('\n');
        }
        return SchemaCacheKeyParts.Sha256Hex(Encoding.UTF8.GetBytes(lines.ToString()));
    }

    public static string RelativeUnixPath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static Entry? TryRead(string path, string key, string schemaId)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int newline = Array.IndexOf(bytes, (byte)'\n');
            if (newline < 0)
            {
                return null;
            }
            Header? header = JsonSerializer.Deserialize<Header>(bytes.AsSpan(0, newline), JsonSerializerOptions.Web);
            byte[] assembly = bytes[(newline + 1)..];
            return header is not null && header.Format == FormatVersion && header.Key == key && header.SchemaId == schemaId &&
                header.AssemblyLength == assembly.Length &&
                header.AssemblySha256 == SchemaCacheKeyParts.Sha256Hex(assembly) &&
                !string.IsNullOrEmpty(header.RootTypeName)
                ? new Entry(assembly, header.RootTypeName)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Write(string path, string key, string schemaId, byte[] assembly, string rootTypeName)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory!);
            byte[] header = JsonSerializer.SerializeToUtf8Bytes(
                new Header(FormatVersion, key, schemaId, rootTypeName, assembly.Length, SchemaCacheKeyParts.Sha256Hex(assembly)),
                JsonSerializerOptions.Web);
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(header);
                stream.WriteByte((byte)'\n');
                stream.Write(assembly);
                stream.Flush(flushToDisk: true);
            }
            // A rename within one directory: whoever reads the path sees the old whole entry or the new whole entry.
            // Overwriting is what replaces an entry that did not check out; when two writers race, the last rename wins
            // and both wrote the same thing.
            File.Move(temporary, path, overwrite: true);
            Prune();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is only ever a shortcut: this run has its compiled schema either way. A rename refused because
            // another process is reading the entry at that instant leaves that entry in place for the next run.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Entries for keys no longer in use -- an earlier tool build, an earlier contract -- are never read again; drop those
    /// not rewritten for a month, once per process and only after this process wrote one.
    /// </summary>
    private void Prune()
    {
        if (pruned)
        {
            return;
        }
        pruned = true;
        DateTime cutoff = DateTime.UtcNow - Retention;
        foreach (string file in Directory.EnumerateFiles(directory!, "*" + Extension))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for a later prune.
        }
    }

    // Ported from Corvus.Json.Validator 4.6.7 JsonSchema.FromCore and RegisterVocabularies: everything up to and including
    // code generation, unchanged.
    private (byte[] Assembly, string RootTypeName) Compile(string schemaId)
    {
        // FromText resolved against [the schema's own text, the additional resolver, the metaschemas]; the validator's
        // additional resolver already holds every vendored schema under its $id, this one included.
        CompoundDocumentResolver resolver = new(schemas, MetaschemaResolver());
        VocabularyRegistry vocabularyRegistry = new();
        Corvus.Json.CodeGeneration.Draft202012.VocabularyAnalyser.RegisterAnalyser(resolver, vocabularyRegistry);
        Corvus.Json.CodeGeneration.Draft201909.VocabularyAnalyser.RegisterAnalyser(resolver, vocabularyRegistry);
        Corvus.Json.CodeGeneration.Draft7.VocabularyAnalyser.RegisterAnalyser(vocabularyRegistry);
        Corvus.Json.CodeGeneration.Draft6.VocabularyAnalyser.RegisterAnalyser(vocabularyRegistry);
        Corvus.Json.CodeGeneration.Draft4.VocabularyAnalyser.RegisterAnalyser(vocabularyRegistry);
        Corvus.Json.CodeGeneration.OpenApi30.VocabularyAnalyser.RegisterAnalyser(vocabularyRegistry);
        vocabularyRegistry.RegisterVocabularies(Corvus.Json.CodeGeneration.CorvusVocabulary.SchemaVocabulary.DefaultInstance);

        JsonSchemaTypeBuilder typeBuilder = new(resolver, vocabularyRegistry);
        TypeDeclaration rootType = typeBuilder.AddTypeDeclarations(
            new JsonReference(schemaId), Corvus.Json.CodeGeneration.Draft202012.VocabularyAnalyser.DefaultVocabulary);
        IReadOnlyCollection<GeneratedCodeFile> generatedCode = typeBuilder.GenerateCodeUsing(
            CSharpLanguageProvider.DefaultWithOptions(new CSharpLanguageProvider.Options("GeneratedCode", alwaysAssertFormat: true)),
            CancellationToken.None,
            rootType);
        string rootTypeName = CSharpLanguageProvider.GetFullyQualifiedDotnetTypeName(rootType.ReducedTypeDeclaration().ReducedType);
        return (Emit(generatedCode, schemaId), rootTypeName);
    }

    // Ported from Corvus.Json.Validator 4.6.7 JsonSchema.CompileGeneratedType (NET8_0_OR_GREATER), up to the emit. The
    // assembly name is derived from the key's schema rather than a fresh Guid, so an entry is the same bytes whoever
    // writes it; it is unique per schema, which is all the load context needs.
    private static byte[] Emit(IReadOnlyCollection<GeneratedCodeFile> generatedCode, string schemaId)
    {
        DependencyContext context = DependencyContext.Default
            ?? throw new InvalidOperationException("Unable to find compilation context.");
        MetadataReference[] references = [.. context.CompileLibraries
            .SelectMany(library => library.ResolveReferencePaths())
            .Select(path => MetadataReference.CreateFromFile(path))];
        string[] defines = [.. context.CompilationOptions.Defines.Union(["DYNAMIC_BUILD"]).OfType<string>()];
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Preview)
            .WithPreprocessorSymbols(defines);
        SyntaxTree[] syntaxTrees =
        [
            CSharpSyntaxTree.ParseText(GlobalUsingStatements, options: parseOptions, path: "GlobalUsingStatements.cs"),
            .. generatedCode.Select(file => CSharpSyntaxTree.ParseText(file.FileContent, options: parseOptions, path: file.FileName))
        ];
        CSharpCompilationOptions options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Driver.GeneratedTypes_" + SchemaCacheKeyParts.Sha256Hex(Encoding.UTF8.GetBytes(schemaId))[..32],
            syntaxTrees, references, options);
        using MemoryStream output = new();
        EmitResult result = compilation.Emit(output);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "Unable to compile generated code" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics));
        }
        return output.ToArray();
    }

    // Ported from Corvus.Json.Validator 4.6.7 JsonSchema.CompileGeneratedType (from the load on) and BuildValidateCallback.
    private CompiledSchema Load(byte[] assembly, string rootTypeName)
    {
        Type schemaType;
        if (rootTypeName.StartsWith("Corvus.", StringComparison.Ordinal))
        {
            // A schema that reduces to one of Corvus's own types is validated by that type, not the generated one.
            schemaType = AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "Corvus.Json.ExtendedTypes")
                .ExportedTypes.Single(type => type.FullName == rootTypeName);
        }
        else
        {
            using MemoryStream stream = new(assembly, writable: false);
            schemaType = loadContext.LoadFromStream(stream).ExportedTypes.Single(type => type.FullName == rootTypeName);
        }
        ConstructorInfo constructor = schemaType.GetConstructors()
            .SingleOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.Name.StartsWith("JsonElement", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unable to find the public JsonElement constructor on type '{schemaType.FullName}'");
        ParameterExpression element = Expression.Parameter(typeof(JsonElement), "jsonElement");
        Func<JsonElement, IJsonValue> construct = Expression.Lambda<Func<JsonElement, IJsonValue>>(
            Expression.Convert(Expression.New(constructor, element), typeof(IJsonValue)), element).Compile();
        return new CompiledSchema((value, level) => construct(value).Validate(ValidationContext.ValidContext, level));
    }

    /// <summary>
    /// Corvus's own metaschema resolver (draft 4 through 2020-12 and its own vocabulary), the instance
    /// <c>JsonSchema.FromText</c> resolves against. Read rather than rebuilt: it is loaded from resources embedded in
    /// Corvus.Json.Validator through an internal extension method. A Corvus that renamed it fails here, loudly, not with
    /// a schema compiled against no metaschema.
    /// </summary>
    private static IDocumentResolver MetaschemaResolver() =>
        typeof(Corvus.Json.Validator.JsonSchema)
            .GetField("MetaschemaDocumentResolver", BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null) as IDocumentResolver
        ?? throw new InvalidOperationException(
            "Corvus.Json.Validator.JsonSchema.MetaschemaDocumentResolver is not there: the Corvus version moved, and " +
            "CompiledSchemaCache, which is ported from Corvus.Json.Validator 4.6.7, has to be checked against it.");

    private sealed record Header(int Format, string Key, string SchemaId, string RootTypeName, int AssemblyLength, string AssemblySha256);

    private sealed record Entry(byte[] Assembly, string RootTypeName);
}
