using Azure.Core;

namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// One scope's Microsoft Entra access token, reused until shortly before it expires. Concurrent
/// callers that find it stale share a single refresh instead of each asking the credential, which
/// for a managed identity is a call to the instance metadata service.
/// </summary>
internal sealed class AccessTokenCache(TokenCredential credential, string scope, TimeProvider time)
{
  /// <summary>How long before expiry a token is refreshed, so no request leaves with one about to lapse.</summary>
  internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

  private readonly TokenRequestContext _context = new([scope]);
  private readonly Lock _lock = new();

  // Replaced as a whole, so readers never see one token's value with another's times.
  private volatile CachedToken? _current;

  // The refresh in flight, or the last one, done; guarded by _lock.
  private Task<CachedToken>? _refreshing;

  /// <summary>
  /// Returns a token valid for at least <see cref="RefreshMargin"/>, or failing a refresh, one that
  /// has not expired yet.
  /// </summary>
  public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
  {
    var current = _current;
    if (current is not null && time.GetUtcNow() < current.RefreshAt)
    {
      return current.Value;
    }

    Task<CachedToken> refreshing;
    lock (_lock)
    {
      if (_refreshing is null || _refreshing.IsCompleted)
      {
        // Another caller may have refreshed it since this one looked.
        current = _current;
        if (current is not null && time.GetUtcNow() < current.RefreshAt)
        {
          return current.Value;
        }

        // Off the lock: a credential may do slow work before its first await.
        _refreshing = Task.Run(RefreshAsync, CancellationToken.None);
      }

      refreshing = _refreshing;
    }

    try
    {
      // The refresh is shared, so no one caller's cancellation stops it; each caller stops waiting
      // on its own.
      return (await refreshing.WaitAsync(cancellationToken)).Value;
    }
    catch (Exception)
      when (!cancellationToken.IsCancellationRequested
        && current is not null
        && time.GetUtcNow() < current.ExpiresOn
      )
    {
      // The refresh failed, but the token is still good for a few minutes: use it, and refresh again
      // on the next call, so a passing identity-endpoint failure does not fail requests.
      return current.Value;
    }
  }

  private async Task<CachedToken> RefreshAsync()
  {
    var token = await credential.GetTokenAsync(_context, CancellationToken.None);
    var refreshAt = token.ExpiresOn - RefreshMargin;
    if (token.RefreshOn is { } refreshOn && refreshOn < refreshAt)
    {
      refreshAt = refreshOn;
    }

    CachedToken cached = new(token.Token, refreshAt, token.ExpiresOn);
    _current = cached;
    return cached;
  }

  private sealed record CachedToken(
    string Value,
    DateTimeOffset RefreshAt,
    DateTimeOffset ExpiresOn
  )
  {
    // Keeps the token out of any string a debugger or a careless log line makes of it.
    public override string ToString() => $"CachedToken {{ RefreshAt = {RefreshAt} }}";
  }
}
