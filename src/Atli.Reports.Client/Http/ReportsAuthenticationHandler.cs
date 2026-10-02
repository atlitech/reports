using System.Net.Http.Headers;

namespace Atli.Reports.Client.Http;

/// <summary>Supplies credentials to conversion attempts, never to health probes.</summary>
internal sealed class ReportsAuthenticationHandler(
  Uri endpoint,
  string? apiKey,
  Func<CancellationToken, ValueTask<string>>? tokenProvider
) : DelegatingHandler
{
  internal const string ApiKeyHeaderName = "X-Reports-Api-Key";
  internal static readonly HttpRequestOptionsKey<bool> Authenticate = new(
    "Atli.Reports.Client.Authenticate"
  );

  protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken
  )
  {
    if (request.Options.TryGetValue(Authenticate, out var authenticate) && authenticate)
    {
      if (
        request.RequestUri is not { IsAbsoluteUri: true } uri
        || !string.Equals(uri.Scheme, endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(uri.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
        || uri.Port != endpoint.Port
      )
      {
        throw new ReportsAuthenticationException(
          "The conversion request does not target the configured reports server."
        );
      }

      if (apiKey is not null)
      {
        request.Headers.Remove(ApiKeyHeaderName);
        request.Headers.Add(ApiKeyHeaderName, apiKey);
      }
      else if (tokenProvider is not null)
      {
        string token;
        try
        {
          token = await tokenProvider(cancellationToken);
        }
        catch (OperationCanceledException)
        {
          throw new OperationCanceledException(
            "The reports access token acquisition was canceled.",
            cancellationToken
          );
        }
        catch (Exception)
        {
          // Providers can include credential values in their exception text. Do not retain it.
          throw new ReportsAuthenticationException(
            "The reports access token could not be acquired."
          );
        }

        if (!IsSafeCredential(token))
        {
          throw new ReportsAuthenticationException(
            "The reports access token provider returned an invalid token."
          );
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      }
    }

    return await base.SendAsync(request, cancellationToken);
  }

  internal static bool IsSafeCredential(string? value) =>
    value is { Length: > 0 and <= 16384 }
    && value.All(character => character is > ' ' and < '\u007f');
}

internal sealed class ReportsAuthenticationException(string message) : Exception(message);
