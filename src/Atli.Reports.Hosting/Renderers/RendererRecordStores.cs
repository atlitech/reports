namespace Atli.Reports.Hosting.Renderers;

/// <summary>Creates the configured <see cref="IRendererRecordStore"/>.</summary>
public static class RendererRecordStores
{
  /// <summary>
  /// Creates the store <paramref name="options"/> names: <see cref="FileRendererRecordStore"/> or
  /// <see cref="KeyVaultRendererRecordStore"/>. Throws <see cref="InvalidOperationException"/> for an
  /// unknown store or a missing setting.
  /// </summary>
  public static IRendererRecordStore Create(RendererRecordStoreOptions options) =>
    throw new NotImplementedException();
}
