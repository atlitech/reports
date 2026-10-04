using Azure.Core;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>
/// A credential that issues <c>token-1</c>, <c>token-2</c>, and so on, each valid for
/// <see cref="Lifetime"/> from the clock's now, and records the scopes it was asked for.
/// </summary>
internal sealed class FakeCredential(TimeProvider clock) : TokenCredential
{
  private readonly Lock _lock = new();
  private readonly List<string> _scopes = [];
  private int _issued;

  public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

  /// <summary>When set, every request for a token fails with it.</summary>
  public Exception? Failure { get; set; }

  /// <summary>When set, requests for a token wait for it before they answer.</summary>
  public Task? Gate { get; set; }

  /// <summary>How many tokens were asked for, including failed requests.</summary>
  public int Requests
  {
    get
    {
      lock (_lock)
      {
        return _scopes.Count;
      }
    }
  }

  public IReadOnlyList<string> Scopes
  {
    get
    {
      lock (_lock)
      {
        return [.. _scopes];
      }
    }
  }

  public override AccessToken GetToken(
    TokenRequestContext requestContext,
    CancellationToken cancellationToken
  ) => throw new NotSupportedException("The client asks for tokens asynchronously.");

  public override async ValueTask<AccessToken> GetTokenAsync(
    TokenRequestContext requestContext,
    CancellationToken cancellationToken
  )
  {
    lock (_lock)
    {
      _scopes.AddRange(requestContext.Scopes);
    }

    if (Gate is { } gate)
    {
      await gate.WaitAsync(cancellationToken);
    }

    if (Failure is { } failure)
    {
      throw failure;
    }

    var number = Interlocked.Increment(ref _issued);
    return new AccessToken($"token-{number}", clock.GetUtcNow() + Lifetime);
  }
}
