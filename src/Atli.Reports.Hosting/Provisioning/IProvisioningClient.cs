namespace Atli.Reports.Hosting.Provisioning;

/// <summary>The gateway's side of the <see cref="ProvisioningApi"/>.</summary>
public interface IProvisioningClient
{
  /// <summary>
  /// Ensures <paramref name="tenantId"/> has a ready renderer and record, and returns whether this
  /// call created them. Throws <see cref="ProvisioningApiException"/> when the service refuses or
  /// fails.
  /// </summary>
  Task<bool> EnsureRendererAsync(string tenantId, CancellationToken cancellationToken);

  /// <summary>
  /// Deletes <paramref name="tenantId"/>'s renderer and record; succeeds when there are none. Throws
  /// <see cref="ProvisioningApiException"/> when the service refuses or fails.
  /// </summary>
  Task DeleteRendererAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>A refusal or failure of the provisioning service, or of reaching it.</summary>
public sealed class ProvisioningApiException : Exception
{
  /// <summary>Creates the exception.</summary>
  public ProvisioningApiException() { }

  /// <summary>Creates the exception.</summary>
  public ProvisioningApiException(string message)
    : base(message) { }

  /// <summary>Creates the exception.</summary>
  public ProvisioningApiException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>
  /// The answer's <see cref="ProvisioningProblemKinds"/> value, or <see langword="null"/> when there
  /// was no problem details answer (the service could not be reached, or answered otherwise).
  /// </summary>
  public string? Kind { get; init; }

  /// <summary>The answer's HTTP status, or <see langword="null"/> when there was no answer.</summary>
  public int? StatusCode { get; init; }

  /// <summary>The answer's <c>Retry-After</c>, when it had one.</summary>
  public TimeSpan? RetryAfter { get; init; }
}
