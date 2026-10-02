namespace Atli.Reports.Engine.Chromium.Network;

/// <summary>Contains no document content, destination URLs, or credentials.</summary>
internal sealed class NetworkPolicyException : Exception
{
  public NetworkPolicyException()
    : base(
      "The document requested an external resource or navigation denied by the rendering network policy."
    ) { }
}
