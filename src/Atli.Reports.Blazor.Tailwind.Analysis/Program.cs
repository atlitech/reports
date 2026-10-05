using Atli.Reports.Blazor.Tailwind.Analysis;
using Atli.Reports.Blazor.Tailwind.Contracts;

if (args.Length != 1)
{
  Console.Error.WriteLine("Usage: dotnet Atli.Reports.Blazor.Tailwind.Analysis.dll <request.json>");
  return 1;
}
try
{
  var request = ManifestIO.Read<DiscoveryRequest>(args[0]);
  var compilation = CompilationReader.Read(request);
  var manifest = ComponentExtractor.Extract(compilation, request);
  ManifestIO.Validate(manifest, request.OutputPath);
  ManifestIO.Write(request.OutputPath, manifest);
  Console.WriteLine(
    $"Tailwind discovery: {manifest.Components.Count} components from {manifest.Artifact.AssemblyName}."
  );
  return 0;
}
catch (Exception exception)
  when (exception
      is IOException
        or InvalidOperationException
        or ArgumentException
        or FormatException
        or BadImageFormatException
        or UnauthorizedAccessException
        or NotSupportedException
  )
{
  Console.Error.WriteLine($"ATLI2000: Tailwind discovery failed: {exception.Message}");
  return 1;
}
