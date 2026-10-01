namespace Aspire.Hosting;

/// <summary>
/// The image <see cref="ReportsServerBuilderExtensions.AddReportsServer"/> runs. The build generates
/// the other half of this class, the <c>Tag</c>, from the package's version (see the project file).
/// </summary>
internal static partial class ReportsServerContainerImageTags
{
  /// <summary>The registry the release publishes the server image to.</summary>
  internal const string Registry = "ghcr.io";

  /// <summary>The server image's repository in <see cref="Registry"/>.</summary>
  internal const string Image = "atlitech/reports-server";
}
