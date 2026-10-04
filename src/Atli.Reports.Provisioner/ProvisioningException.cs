namespace Atli.Reports.Provisioner;

/// <summary>
/// A command could not do what it was asked, for a reason its message states. Messages name tenants,
/// sandboxes, and disk images, never credentials.
/// </summary>
internal sealed class ProvisioningException : Exception
{
  public ProvisioningException() { }

  public ProvisioningException(string message)
    : base(message) { }

  public ProvisioningException(string message, Exception innerException)
    : base(message, innerException) { }
}
