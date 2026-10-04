using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Provisioner;

/// <summary>
/// The labels every renderer sandbox carries. They let a rollout keep a renderer's size and let
/// <c>delete</c> and <c>list</c> find renderer sandboxes no record points to. Labels are immutable.
/// </summary>
internal static class RendererLabels
{
  public const string App = "app";
  public const string AppValue = "atli-reports";
  public const string Role = "role";
  public const string RoleValue = "renderer";
  public const string Tenant = "tenant";
  public const string Size = "size";

  public static IReadOnlyDictionary<string, string> For(string tenantId, RendererSize size) =>
    new Dictionary<string, string>
    {
      [App] = AppValue,
      [Role] = RoleValue,
      [Tenant] = tenantId,
      [Size] = size.Name,
    };

  /// <summary>Whether the provisioner created <paramref name="sandbox"/> as a renderer.</summary>
  public static bool IsRenderer(SandboxView sandbox) =>
    sandbox.Labels.TryGetValue(App, out var app)
    && app == AppValue
    && sandbox.Labels.TryGetValue(Role, out var role)
    && role == RoleValue;

  /// <summary>The tenant a renderer sandbox serves, or <see langword="null"/> for other sandboxes.</summary>
  public static string? TenantOf(SandboxView sandbox) =>
    IsRenderer(sandbox) && sandbox.Labels.TryGetValue(Tenant, out var tenant) ? tenant : null;

  /// <summary>The size label of <paramref name="sandbox"/>, or <see langword="null"/>.</summary>
  public static string? SizeOf(SandboxView sandbox) =>
    sandbox.Labels.TryGetValue(Size, out var size) ? size : null;
}
