using System.Net;
using System.Text;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>A request as the fake data plane received it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The absolute request URI.</param>
/// <param name="Authorization">The <c>Authorization</c> header.</param>
/// <param name="ContentType">The body's content type, when there is a body.</param>
/// <param name="Body">The body, when there is one.</param>
internal sealed record ReceivedRequest(
  HttpMethod Method,
  Uri Uri,
  string? Authorization,
  string? ContentType,
  string? Body
)
{
  /// <summary>The path after the group's address, such as <c>sandboxes/abc</c>.</summary>
  public string PathInGroup => Uri.AbsolutePath[FakeDataPlane.GroupPath.Length..];
}

/// <summary>
/// A stand-in for the Sandboxes data plane: an <see cref="HttpMessageHandler"/> that records every
/// request and answers each with the next of its scripted answers (the last one repeats), so a
/// <see cref="SandboxesClient"/> runs whole with no network.
/// </summary>
internal sealed class FakeDataPlane : HttpMessageHandler
{
  public const string GroupPath =
    "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/";

  public const string ApiVersionQuery = "?api-version=2026-02-01-preview";

  public static readonly SandboxesOptions Options = new()
  {
    SubscriptionId = "11111111-2222-3333-4444-555555555555",
    ResourceGroup = "rg-1",
    SandboxGroup = "group-1",
    Region = "eastus2",
  };

  private readonly Lock _lock = new();
  private readonly List<ReceivedRequest> _requests = [];
  private readonly Queue<Func<ReceivedRequest, HttpResponseMessage>> _answers = [];
  private Func<ReceivedRequest, HttpResponseMessage>? _last;

  public FakeDataPlane(params Func<ReceivedRequest, HttpResponseMessage>[] answers)
  {
    foreach (var answer in answers)
    {
      _answers.Enqueue(answer);
    }
  }

  public IReadOnlyList<ReceivedRequest> Requests
  {
    get
    {
      lock (_lock)
      {
        return [.. _requests];
      }
    }
  }

  /// <summary>Answers with <paramref name="status"/> and <paramref name="json"/>.</summary>
  public static Func<ReceivedRequest, HttpResponseMessage> Json(
    HttpStatusCode status,
    string json,
    string contentType = "application/json",
    string? retryAfter = null
  ) =>
    _ =>
    {
      HttpResponseMessage response = new(status)
      {
        Content = new StringContent(json, Encoding.UTF8, contentType),
      };
      if (retryAfter is not null)
      {
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
      }

      return response;
    };

  /// <summary>Answers <c>200</c> with <paramref name="json"/>.</summary>
  public static Func<ReceivedRequest, HttpResponseMessage> Ok(string json) =>
    Json(HttpStatusCode.OK, json);

  /// <summary>Answers with <paramref name="status"/> and the data plane's problem details.</summary>
  public static Func<ReceivedRequest, HttpResponseMessage> Problem(
    HttpStatusCode status,
    string title,
    string detail,
    string? retryAfter = null
  ) =>
    Json(
      status,
      $$"""{"title":"{{title}}","status":{{(int)status}},"detail":"{{detail}}","errorCode":1}""",
      "application/problem+json",
      retryAfter
    );

  /// <summary>Answers with <paramref name="status"/> and no body.</summary>
  public static Func<ReceivedRequest, HttpResponseMessage> Status(HttpStatusCode status) =>
    _ => new HttpResponseMessage(status);

  /// <summary>Fails as a connection that could not be made does.</summary>
  public static Func<ReceivedRequest, HttpResponseMessage> Unreachable() =>
    _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");

  /// <summary>A sandbox as the data plane writes it, with the fields the client ignores.</summary>
  public static string Sandbox(string id, string state, string ports = "[]") =>
    $$"""
      {
        "id": "{{id}}",
        "labels": { "tenant": "acme" },
        "vmmType": "cloudhypervisor",
        "sourcesRef": { "diskImage": { "id": "disk-1", "isPublic": false } },
        "resources": { "cpu": "1000m", "memory": "2048Mi", "disk": "10240Mi" },
        "createdAt": "2026-10-04T01:50:55.1020312+00:00",
        "state": "{{state}}",
        "ports": {{ports}},
        "egressPolicy": { "defaultAction": "Deny", "hostRules": [] },
        "lifecycle": { "autoSuspendPolicy": { "enabled": true, "interval": 300, "mode": "Memory" } },
        "region": "eastus2"
      }
      """;

  /// <summary>An anonymous port as the data plane writes it.</summary>
  public static string Port(string id, int port) =>
    $$"""
      {
        "port": {{port}},
        "url": "https://{{id}}--{{port}}.eastus2.adcproxy.io",
        "auth": { "anonymous": true },
        "activationMode": "Manual",
        "protocol": "Http"
      }
      """;

  protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken
  )
  {
    var body = request.Content is null
      ? null
      : await request.Content.ReadAsStringAsync(cancellationToken);
    ReceivedRequest received = new(
      request.Method,
      request.RequestUri!,
      request.Headers.Authorization?.ToString(),
      request.Content?.Headers.ContentType?.ToString(),
      body
    );
    Func<ReceivedRequest, HttpResponseMessage> answer;
    lock (_lock)
    {
      _requests.Add(received);
      if (_answers.TryDequeue(out var next))
      {
        _last = next;
      }

      answer =
        _last ?? throw new InvalidOperationException("The fake data plane has no answer scripted.");
    }

    return answer(received);
  }
}
