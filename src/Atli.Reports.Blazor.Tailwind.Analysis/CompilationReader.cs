using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Atli.Reports.Blazor.Tailwind.Analysis;

/// <summary>Reconstructs an existing compilation without invoking a compiler or source generator.</summary>
public static class CompilationReader
{
  private static readonly Guid OptionsId = new("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");
  private static readonly Guid ReferencesId = new("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D");
  private static readonly Guid EmbeddedSourceId = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
  private static readonly Guid Sha256Id = new("8829d00f-11b8-4213-878b-770e8597ac16");
  private static readonly Guid Sha1Id = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");

  public static CSharpCompilation Read(DiscoveryRequest request)
  {
    var snapshot = ManifestIO.Read<CompilerSnapshot>(request.CompilerArgumentsPath);
    if (Path.GetFullPath(snapshot.ProjectDirectory) != Path.GetFullPath(request.ProjectDirectory))
      throw new InvalidDataException(
        "Compiler snapshot belongs to a different project directory. Rebuild the project."
      );
    var arguments = snapshot.Arguments;
    var parsed = CSharpCommandLineParser.Default.Parse(
      arguments,
      request.ProjectDirectory,
      sdkDirectory: null
    );
    var parseErrors = parsed
      .Errors.Where(error => error.Severity == DiagnosticSeverity.Error)
      .ToArray();
    if (parseErrors.Length != 0)
      throw new InvalidDataException(
        "Cannot replay this compiler snapshot: "
          + string.Join("; ", parseErrors.Select(error => error.ToString()))
      );

    using var assemblyStream = File.OpenRead(request.AssemblyPath);
    using var pe = new PEReader(assemblyStream);
    if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
      throw new InvalidDataException("Discovery requires a managed implementation assembly.");
    if (
      !pe.TryOpenAssociatedPortablePdb(
        request.AssemblyPath,
        path => File.Exists(path) ? File.OpenRead(path) : null,
        out var pdbProvider,
        out _
      )
    )
      throw new InvalidDataException(
        "A matching portable or embedded PDB is required for automatic Tailwind discovery. Build with DebugType=portable or embedded, or use explicit @source declarations."
      );
    using (pdbProvider)
    {
      var pdb = pdbProvider!.GetMetadataReader();
      var options = ReadOptions(pdb);
      if (
        !options.TryGetValue("source-file-count", out var sourceCountText)
        || !int.TryParse(sourceCountText, out var sourceCount)
        || sourceCount < parsed.SourceFiles.Length
        || sourceCount > pdb.Documents.Count
      )
        throw new InvalidDataException(
          "The PDB does not expose a supported, exact compilation source inventory."
        );
      ValidateOptions(options, parsed);
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      var defaultEncoding =
        parsed.Encoding
        ?? (
          options.TryGetValue("fallback-encoding", out var fallback)
            ? Encoding.GetEncoding(fallback)
            : Encoding.UTF8
        );

      // PDB source documents are ordered as original trees followed by generated trees. The
      // remaining documents can be #line destinations and must not become extra source trees.
      var sourceTrees = new List<SyntaxTree>();
      var documents = pdb.Documents.Take(sourceCount).ToArray();
      for (var index = 0; index < documents.Length; index++)
      {
        var document = pdb.GetDocument(documents[index]);
        var documentPath = pdb.GetString(document.Name);
        var embedded = FindBlob(pdb, documents[index], EmbeddedSourceId);
        byte[] bytes;
        if (embedded is not null)
          bytes = DecodeEmbedded(embedded);
        else if (index < parsed.SourceFiles.Length)
          bytes = File.ReadAllBytes(
            Path.GetFullPath(parsed.SourceFiles[index].Path, request.ProjectDirectory)
          );
        else
          throw new InvalidDataException(
            $"Generated source '{documentPath}' is not embedded in the PDB; an exact source inventory cannot be reconstructed."
          );

        var hashAlgorithm = pdb.GetGuid(document.HashAlgorithm);
#pragma warning disable CA5350 // SHA-1 is required only to validate existing Portable PDB source-checksum records.
        var actualHash =
          hashAlgorithm == Sha256Id ? SHA256.HashData(bytes)
          : hashAlgorithm == Sha1Id ? SHA1.HashData(bytes)
          : throw new InvalidDataException(
            $"Unsupported checksum algorithm for source '{documentPath}'."
          );
#pragma warning restore CA5350
        if (!actualHash.AsSpan().SequenceEqual(pdb.GetBlobBytes(document.Hash)))
          throw new InvalidDataException(
            $"Source checksum mismatch for '{documentPath}'. Rebuild before extracting Tailwind dependencies."
          );
        using var stream = new MemoryStream(bytes, writable: false);
        var source = SourceText.From(
          stream,
          defaultEncoding,
          hashAlgorithm == Sha1Id ? SourceHashAlgorithm.Sha1 : SourceHashAlgorithm.Sha256
        );
        sourceTrees.Add(CSharpSyntaxTree.ParseText(source, parsed.ParseOptions, documentPath));
      }

      var references = ReadReferences(pdb, parsed, request.ProjectDirectory);
      var assemblyMetadata = pe.GetMetadataReader();
      var definition = assemblyMetadata.GetAssemblyDefinition();
      var assemblyName = assemblyMetadata.GetString(definition.Name);
      // Reproduce signing identity from public metadata without opening private signing keys.
      var compilationOptions = parsed
        .CompilationOptions.WithCryptoKeyFile(null)
        .WithCryptoKeyContainer(null)
        .WithCryptoPublicKey(
          ImmutableArray.Create(assemblyMetadata.GetBlobBytes(definition.PublicKey))
        )
        .WithDelaySign(null)
        .WithPublicSign(false);
      var compilation = CSharpCompilation.Create(
        assemblyName,
        sourceTrees,
        references,
        compilationOptions
      );
      var diagnostics = compilation
        .GetDiagnostics()
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .Take(8)
        .ToArray();
      if (diagnostics.Length != 0)
        throw new InvalidDataException(
          "Reconstructed compilation does not bind successfully; discovery cannot safely continue. "
            + string.Join(
              Environment.NewLine,
              diagnostics.Select(diagnostic => diagnostic.ToString())
            )
        );
      return compilation;
    }
  }

  private static Dictionary<string, string> ReadOptions(MetadataReader pdb)
  {
    var data =
      FindBlob(pdb, EntityHandle.ModuleDefinition, OptionsId)
      ?? throw new InvalidDataException("PDB compilation options are missing.");
    var values = Encoding.UTF8.GetString(data).Split('\0');
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index + 1 < values.Length; index += 2)
      if (values[index].Length > 0)
        result.Add(values[index], values[index + 1]);
    return result;
  }

  private static void ValidateOptions(
    Dictionary<string, string> options,
    CSharpCommandLineArguments parsed
  )
  {
    if (!options.TryGetValue("language", out var language) || language != "C#")
      throw new InvalidDataException("Only C# Razor projects are supported by Tailwind discovery.");
    if (
      !options.TryGetValue("language-version", out var languageVersion)
      || !LanguageVersionFacts.TryParse(languageVersion, out var pdbVersion)
      || pdbVersion != parsed.ParseOptions.LanguageVersion
    )
      throw new InvalidDataException("Compiler snapshot language version differs from the PDB.");
    var defines = options.GetValueOrDefault("define", "");
    if (
      !defines
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Order(StringComparer.Ordinal)
        .SequenceEqual(parsed.ParseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal))
    )
      throw new InvalidDataException("Compiler snapshot conditional symbols differ from the PDB.");
    if (
      !bool.TryParse(options.GetValueOrDefault("unsafe", "false"), out var allowUnsafe)
      || allowUnsafe != parsed.CompilationOptions.AllowUnsafe
      || !bool.TryParse(options.GetValueOrDefault("checked", "false"), out var checkOverflow)
      || checkOverflow != parsed.CompilationOptions.CheckOverflow
      || !options
        .GetValueOrDefault("nullable", "Disable")
        .Equals(
          parsed.CompilationOptions.NullableContextOptions.ToString(),
          StringComparison.OrdinalIgnoreCase
        )
    )
      throw new InvalidDataException(
        "Compiler snapshot safety or nullable options differ from the PDB."
      );
    if (
      options.GetValueOrDefault("output-kind") != parsed.CompilationOptions.OutputKind.ToString()
      || options.GetValueOrDefault("platform") != parsed.CompilationOptions.Platform.ToString()
    )
      throw new InvalidDataException(
        "Compiler snapshot output kind or platform differs from the PDB."
      );
    var optimization = options.GetValueOrDefault("optimization", "debug");
    if (
      optimization is not ("debug" or "debug-plus" or "release" or "release-debug-plus")
      || optimization.StartsWith("release", StringComparison.Ordinal)
        != (parsed.CompilationOptions.OptimizationLevel == OptimizationLevel.Release)
    )
      throw new InvalidDataException("Compiler snapshot optimization level differs from the PDB.");
    if (
      options.TryGetValue("default-encoding", out var encoding)
      && !encoding.Equals(parsed.Encoding?.WebName, StringComparison.OrdinalIgnoreCase)
    )
      throw new InvalidDataException("Compiler snapshot source encoding differs from the PDB.");
  }

  private static ImmutableArray<MetadataReference> ReadReferences(
    MetadataReader pdb,
    CSharpCommandLineArguments parsed,
    string directory
  )
  {
    var referenceBlob = pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
      .Select(handle => pdb.GetCustomDebugInformation(handle))
      .SingleOrDefault(info => pdb.GetGuid(info.Kind) == ReferencesId);
    if (referenceBlob.Value.IsNil)
      throw new InvalidDataException("PDB reference identities are missing.");
    var reader = pdb.GetBlobReader(referenceBlob.Value);
    var recorded = new List<(Guid Mvid, string Name, string Aliases, byte Flags)>();
    while (reader.RemainingBytes > 0)
    {
      var name = ReadNullTerminated(ref reader);
      var aliases = ReadNullTerminated(ref reader);
      var flags = reader.ReadByte();
      reader.ReadInt32(); // COFF timestamp
      reader.ReadInt32(); // PE image size
      recorded.Add((reader.ReadGuid(), name, aliases, flags));
    }
    var references = ImmutableArray.CreateBuilder<MetadataReference>();
    foreach (var reference in parsed.MetadataReferences)
    {
      var path = Path.GetFullPath(reference.Reference, directory);
      using var stream = File.OpenRead(path);
      using var pe = new PEReader(stream);
      var metadata = pe.GetMetadataReader();
      var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
      var index = recorded.FindIndex(entry =>
        entry.Mvid == mvid && entry.Name == Path.GetFileName(path)
      );
      if (index < 0)
        throw new InvalidDataException(
          $"Reference '{path}' does not match the exact PDB reference identity. Rebuild the project."
        );
      var expected = recorded[index];
      var actualAliases = reference.Properties.Aliases.IsEmpty
        ? new[] { "global" }
        : reference.Properties.Aliases.ToArray();
      var expectedAliases =
        expected.Aliases.Length == 0 ? new[] { "global" } : expected.Aliases.Split(',');
      if (
        !actualAliases
          .Order(StringComparer.Ordinal)
          .SequenceEqual(expectedAliases.Order(StringComparer.Ordinal))
        || reference.Properties.EmbedInteropTypes != ((expected.Flags & 2) != 0)
        || reference.Properties.Kind
          != ((expected.Flags & 1) != 0 ? MetadataImageKind.Assembly : MetadataImageKind.Module)
      )
        throw new InvalidDataException($"Reference alias or interop options differ for '{path}'.");
      references.Add(MetadataReference.CreateFromFile(path, reference.Properties));
      recorded.RemoveAt(index);
    }
    if (recorded.Count != 0)
      throw new InvalidDataException(
        "Compiler snapshot omits one or more references recorded in the PDB."
      );
    return references.ToImmutable();
  }

  private static string ReadNullTerminated(ref BlobReader reader)
  {
    var length = reader.IndexOf(0);
    if (length < 0)
      throw new InvalidDataException("Malformed PDB compilation metadata.");
    var value = reader.ReadUTF8(length);
    reader.ReadByte();
    return value;
  }

  private static byte[]? FindBlob(MetadataReader reader, EntityHandle entity, Guid kind)
  {
    foreach (var handle in reader.GetCustomDebugInformation(entity))
    {
      var information = reader.GetCustomDebugInformation(handle);
      if (reader.GetGuid(information.Kind) == kind)
        return reader.GetBlobBytes(information.Value);
    }
    return null;
  }

  private static byte[] DecodeEmbedded(byte[] data)
  {
    if (data.Length < sizeof(int))
      throw new InvalidDataException("Malformed embedded PDB source.");
    var size = BitConverter.ToInt32(data, 0);
    if (size == 0)
      return data[sizeof(int)..];
    if (size < 0 || size > 64 * 1024 * 1024)
      throw new InvalidDataException("Embedded source exceeds the supported size limit.");
    using var compressed = new MemoryStream(
      data,
      sizeof(int),
      data.Length - sizeof(int),
      writable: false
    );
    using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
    var result = new byte[size];
    deflate.ReadExactly(result);
    if (deflate.ReadByte() != -1)
      throw new InvalidDataException("Embedded source length does not match its PDB record.");
    return result;
  }
}
