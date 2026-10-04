namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// One customer's renderer, as the provisioner records it and the gateway routes to it. The record
/// holds the raw credential the gateway presents, so stores keep it secret.
/// </summary>
public sealed record RendererRecord
{
  /// <summary>The product tenant the renderer serves for its whole lifetime; see <see cref="TenantId"/>.</summary>
  public required string TenantId { get; init; }

  /// <summary>The renderer's base address, which serves <c>POST /convert</c> and <c>/health/ready</c>.</summary>
  public required Uri Url { get; init; }

  /// <summary>
  /// The complete credential the gateway sends in <c>X-Reports-Api-Key</c>. Unique to this renderer:
  /// the renderer holds only its verifier.
  /// </summary>
  public required string ApiKey { get; init; }

  /// <summary>The Sandboxes sandbox ID, when the renderer is a sandbox; the gateway resumes it.</summary>
  public string? SandboxId { get; init; }

  /// <summary>The disk image the renderer was created from; a rollout replaces older ones.</summary>
  public string? DiskImageId { get; init; }

  /// <summary>When the provisioner created the renderer.</summary>
  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>Describes the record without <see cref="ApiKey"/>, so it can be logged and printed.</summary>
  public override string ToString() =>
    $"RendererRecord {{ TenantId = {TenantId}, Url = {Url}, SandboxId = {SandboxId}, "
    + $"DiskImageId = {DiskImageId}, "
    + $"CreatedAt = {CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)} }}";
}
