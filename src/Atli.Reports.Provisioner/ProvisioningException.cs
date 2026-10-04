namespace Atli.Reports.Provisioner;

/// <summary>
/// A command could not do what it was asked, for a reason its message states. Messages name tenants,
/// sandboxes, and disk images, never credentials.
/// </summary>
internal class ProvisioningException : Exception
{
  public ProvisioningException() { }

  public ProvisioningException(string message)
    : base(message) { }

  public ProvisioningException(string message, Exception innerException)
    : base(message, innerException) { }
}

/// <summary>
/// A delete from the provisioning service was refused, and nothing was deleted, because a sandbox of
/// the tenant is disabled: the operator's kill switch keeps it for investigation.
/// </summary>
internal sealed class RendererDisabledException : ProvisioningException
{
  public RendererDisabledException() { }

  public RendererDisabledException(string message)
    : base(message) { }

  public RendererDisabledException(string message, Exception innerException)
    : base(message, innerException) { }
}
