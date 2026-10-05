using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Core;

namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// <see cref="ISandboxesClient"/> over the data plane's REST API, authenticated with Microsoft Entra
/// tokens for <see cref="SandboxesOptions.TokenScope"/>, cached until shortly before they expire.
/// </summary>
/// <remarks>
/// <para>
/// Every call but creation is retried up to three times after a <c>429</c>, <c>502</c>, <c>503</c>,
/// or <c>504</c>, a transport failure, or the HTTP client's timeout: after the
/// response's <c>Retry-After</c> (at most 30 seconds), or else after 1, 2, then 4 seconds. Creation
/// is never retried: a create whose response was lost may still have made a sandbox, and a second
/// attempt would leave a running renderer nobody records.
/// </para>
/// <para>
/// A stop, resume, or port the data plane refuses with <c>409</c> because it is already done
/// succeeds. A <c>409</c> while the sandbox is between states is waited out for up to
/// <see cref="SettleTimeout"/>, and the call is made once more if the sandbox settled where it
/// still applies.
/// </para>
/// <para>
/// Failures throw <see cref="SandboxesException"/> with the status and the data plane's own
/// description of the error, never a request's body or headers: a create's body holds the
/// renderer's environment, credentials among them. The client logs nothing.
/// </para>
/// </remarks>
public sealed class SandboxesClient : ISandboxesClient
{
  /// <summary>Retries after the first attempt, for every call but creation.</summary>
  internal const int MaxRetries = 3;

  /// <summary>The longest <c>Retry-After</c> the client waits; longer ones are cut to this.</summary>
  internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

  /// <summary>The first back-off without a <c>Retry-After</c>; each retry doubles it.</summary>
  internal static readonly TimeSpan FirstBackOff = TimeSpan.FromSeconds(1);

  /// <summary>How much of the data plane's error description an exception message carries.</summary>
  internal const int MaxErrorLength = 500;

  /// <summary>Sandboxes per page of a listing: the most the data plane allows.</summary>
  internal const int ListPageSize = 100;

  /// <summary>
  /// The most pages a listing follows, 100,000 sandboxes: a bound on a data plane whose next links
  /// never end.
  /// </summary>
  internal const int MaxListPages = 1000;

  /// <summary>
  /// How long a call the data plane refused with <c>409</c> waits for the sandbox to settle before
  /// deciding; see <see cref="SettleConflictAsync"/>. Suspending took 7 to 15 seconds in the measured
  /// runs.
  /// </summary>
  internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

  /// <summary>How often a sandbox that is between states is read again.</summary>
  internal static readonly TimeSpan SettlePollInterval = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Environment values at least this long are scrubbed from a failed create's error. Shorter ones
  /// (a concurrency of <c>2</c>, a mode such as <c>ApiKey</c>) hold no credential, and replacing
  /// them would garble the message.
  /// </summary>
  internal const int MinRedactedLength = 8;

  private const string Redacted = "[redacted]";

  private static readonly byte[] EmptyObject = "{}"u8.ToArray();

  private readonly HttpClient _httpClient;
  private readonly AccessTokenCache _tokens;
  private readonly Uri _groupUri;
  private readonly TimeProvider _time;

  /// <summary>Creates the client.</summary>
  /// <param name="httpClient">The client to send requests with; its base address is ignored.</param>
  /// <param name="credential">The credential tokens come from.</param>
  /// <param name="options">The sandbox group.</param>
  public SandboxesClient(
    HttpClient httpClient,
    TokenCredential credential,
    SandboxesOptions options
  )
    : this(httpClient, credential, options, TimeProvider.System) { }

  /// <summary>Creates the client with its retry delays and token expiry on <paramref name="time"/>.</summary>
  internal SandboxesClient(
    HttpClient httpClient,
    TokenCredential credential,
    SandboxesOptions options,
    TimeProvider time
  )
  {
    ArgumentNullException.ThrowIfNull(httpClient);
    ArgumentNullException.ThrowIfNull(credential);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(time);
    options.Validate();
    _httpClient = httpClient;
    _tokens = new AccessTokenCache(credential, SandboxesOptions.TokenScope, time);
    _groupUri = options.GroupUri;
    _time = time;
  }

  /// <inheritdoc />
  /// <remarks>The data plane answers once the sandbox is running.</remarks>
  public async Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(spec);
    var body = JsonSerializer.SerializeToUtf8Bytes(
      ToRequest(spec),
      SandboxesJsonContext.Default.CreateSandboxRequest
    );
    // Should the data plane ever echo the request in an error, the environment's values are
    // scrubbed from the message.
    Call call = new(
      HttpMethod.Put,
      "sandboxes",
      body,
      Retry: false,
      Secrets: spec.Environment.Values
    );
    using var response = await SendAsync(call, cancellationToken);
    await EnsureSuccessAsync(call, response, cancellationToken);
    return await ReadSandboxAsync(call, response, cancellationToken);
  }

  /// <inheritdoc />
  public async Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken)
  {
    Call call = new(HttpMethod.Get, SandboxPath(sandboxId));
    var sandbox = await GetResponseAsync(call, cancellationToken);
    return sandbox is null ? null : ToView(call, sandbox);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// The data plane lists a group a page at a time. On <see cref="SandboxesOptions.ApiVersion"/> it
  /// answers with the first page alone, 25 sandboxes, and no way to the rest, so the client lists on
  /// <see cref="SandboxesOptions.ListApiVersion"/>, in pages of <see cref="ListPageSize"/>, and
  /// follows each page's <c>nextLink</c>. Each page is retried as any other call.
  /// </para>
  /// <para>
  /// The token goes with every page, so a next link must name the first page's address (the same
  /// scheme, host, port, and path); any other link fails the listing, and so does one already
  /// followed, or more than <see cref="MaxListPages"/> pages. A sandbox on two pages, which a group
  /// that changes while it is listed can cause, is listed once, as the later page has it. An answer
  /// that is a bare array, without a next link, is taken whole only when it is shorter than a page:
  /// a full one may be the first page of more.
  /// </para>
  /// </remarks>
  public async Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken)
  {
    Call call = new(HttpMethod.Get, "sandboxes");
    Uri first = new(
      _groupUri,
      "sandboxes?api-version="
        + SandboxesOptions.ListApiVersion
        + "&pageSize="
        + ListPageSize.ToString(CultureInfo.InvariantCulture)
    );
    Dictionary<string, SandboxView> sandboxes = new(StringComparer.Ordinal);
    List<string> order = [];
    HashSet<string> followed = new(StringComparer.Ordinal) { first.AbsoluteUri };
    Uri? page = first;
    for (var pages = 1; page is not null; pages++)
    {
      if (pages > MaxListPages)
      {
        throw new SandboxesException(
          $"{call} answered with more than {MaxListPages} pages; the listing is not read whole."
        );
      }

      using var response = await SendAsync(call, page, cancellationToken);
      await EnsureSuccessAsync(call, response, cancellationToken);
      var (items, nextLink) = await ReadPageAsync(call, response, cancellationToken);
      foreach (var item in items)
      {
        var view = ToView(call, item);
        if (!sandboxes.ContainsKey(view.Id))
        {
          order.Add(view.Id);
        }

        sandboxes[view.Id] = view;
      }

      page = nextLink is null ? null : NextPage(call, first, nextLink, followed);
    }

    return [.. order.Select(id => sandboxes[id])];
  }

  /// <summary>One page of the listing: its sandboxes, and the next page's link if there is one.</summary>
  private static async Task<(List<SandboxResponse> Items, string? NextLink)> ReadPageAsync(
    Call call,
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      using var document = await JsonDocument.ParseAsync(
        stream,
        cancellationToken: cancellationToken
      );
      var root = document.RootElement;
      if (root.ValueKind == JsonValueKind.Array)
      {
        var items =
          root.Deserialize(SandboxesJsonContext.Default.ListSandboxResponse)
          ?? throw Unexpected(call);
        if (items.Count >= ListPageSize)
        {
          throw new SandboxesException(
            $"{call} answered with a full page of {items.Count} sandboxes and no link to the next; "
              + "the listing is not read whole."
          );
        }

        return (items, null);
      }

      var page =
        root.Deserialize(SandboxesJsonContext.Default.SandboxPageResponse)
        ?? throw Unexpected(call);
      return (
        page.Value ?? throw Unexpected(call),
        string.IsNullOrEmpty(page.NextLink) ? null : page.NextLink
      );
    }
    catch (JsonException exception)
    {
      throw Unexpected(call, exception);
    }
  }

  /// <summary>
  /// The next page's address, checked to be the first page's (scheme, host, port, and path), so the
  /// token is never sent elsewhere, and not followed before.
  /// </summary>
  private static Uri NextPage(Call call, Uri first, string nextLink, HashSet<string> followed)
  {
    if (
      !Uri.TryCreate(nextLink, UriKind.Absolute, out var next)
      || Uri.Compare(
        next,
        first,
        UriComponents.SchemeAndServer | UriComponents.Path,
        UriFormat.UriEscaped,
        StringComparison.OrdinalIgnoreCase
      ) != 0
    )
    {
      throw new SandboxesException(
        $"{call} answered with a next page at another address; the listing is not read whole."
      );
    }

    if (!followed.Add(next.AbsoluteUri))
    {
      throw new SandboxesException(
        $"{call} answered with a next page it had already given; the listing is not read whole."
      );
    }

    return next;
  }

  /// <inheritdoc />
  public async Task DeleteAsync(string sandboxId, CancellationToken cancellationToken)
  {
    Call call = new(HttpMethod.Delete, SandboxPath(sandboxId));
    using var response = await SendAsync(call, cancellationToken);
    // The data plane answers 204 for a sandbox that does not exist; a 404 means the same.
    if (response.StatusCode != HttpStatusCode.NotFound)
    {
      await EnsureSuccessAsync(call, response, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Returns once the sandbox is stopped, which takes several seconds. Stopping a stopped sandbox
  /// succeeds.
  /// </remarks>
  public async Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken)
  {
    Call call = new(HttpMethod.Post, SandboxPath(sandboxId) + "/stop", EmptyObject);
    for (var attempt = 0; ; attempt++)
    {
      using (var response = await SendAsync(call, cancellationToken))
      {
        var conflict = await SettleConflictAsync(
          response,
          sandboxId,
          sandbox => sandbox.State == SandboxStates.Stopped,
          IsSettled,
          cancellationToken
        );
        if (conflict.Done is { } stopped)
        {
          return stopped;
        }

        if (conflict.TryAgain && attempt == 0)
        {
          continue;
        }

        await EnsureSuccessAsync(call, response, cancellationToken);
      }

      // The response describes the memory snapshot the stop took (its "id" is the snapshot's), not
      // the sandbox, so the sandbox is read afresh.
      return await GetRequiredAsync(call, sandboxId, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>Takes a second or two. Resuming a running sandbox succeeds.</remarks>
  public async Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken)
  {
    Call call = new(HttpMethod.Post, SandboxPath(sandboxId) + "/resume", EmptyObject);
    for (var attempt = 0; ; attempt++)
    {
      using var response = await SendAsync(call, cancellationToken);
      var conflict = await SettleConflictAsync(
        response,
        sandboxId,
        sandbox => sandbox.State == SandboxStates.Running,
        IsSettled,
        cancellationToken
      );
      if (conflict.Done is { } running)
      {
        return running;
      }

      // Refused while it was suspending, say, and now it has stopped: resuming it can work.
      if (conflict.TryAgain && attempt == 0)
      {
        continue;
      }

      await EnsureSuccessAsync(call, response, cancellationToken);
      return await ReadSandboxAsync(call, response, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// The source ranges become IP access rules that allow them, in order, at most
  /// <see cref="SandboxPortOptions.MaxCidrsPerRule"/> to a rule, with every other address denied.
  /// </para>
  /// <para>
  /// Adding a port the sandbox already exposes with the same access and activation succeeds (and
  /// the same source ranges, when the data plane reports them); otherwise the data plane's
  /// <c>409</c> stands.
  /// </para>
  /// </remarks>
  public async Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    SandboxPortOptions options,
    CancellationToken cancellationToken
  )
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
    ArgumentNullException.ThrowIfNull(options);
    options.Validate();
    var body = JsonSerializer.SerializeToUtf8Bytes(
      new AddPortRequest
      {
        Port = port,
        Auth = new PortAuthWire { Anonymous = options.Anonymous },
        ActivationMode =
          options.Activation == SandboxPortActivation.OnDemand ? "OnDemand" : "Manual",
        IpAccessControl = ToIpAccessControl(options.AllowedSourceCidrs),
      },
      SandboxesJsonContext.Default.AddPortRequest
    );
    Call call = new(HttpMethod.Post, SandboxPath(sandboxId) + "/ports/add", body);
    for (var attempt = 0; ; attempt++)
    {
      SandboxResponse added;
      using (var response = await SendAsync(call, cancellationToken))
      {
        var conflict = await SettleConflictAsync(
          response,
          sandboxId,
          sandbox => sandbox.Ports.Any(exposed => IsExposedAs(exposed, port, options)),
          // The port exists but has no address yet: the proxy is still setting it up.
          sandbox =>
            IsSettled(sandbox)
            && !(sandbox.Ports ?? []).Any(exposed => exposed.Port == port && exposed.Url is null),
          cancellationToken
        );
        if (conflict.Done is { } exposed)
        {
          return exposed;
        }

        if (conflict.TryAgain && attempt == 0)
        {
          continue;
        }

        await EnsureSuccessAsync(call, response, cancellationToken);
        added = await ReadAsync(
          call,
          response,
          SandboxesJsonContext.Default.SandboxResponse,
          cancellationToken
        );
      }

      // The data plane answers with the sandbox's ports alone, so the sandbox is read afresh unless
      // the answer is a whole sandbox.
      return added is { Id: not null, State: not null }
        ? ToView(call, added)
        : await GetRequiredAsync(call, sandboxId, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>Takes the sandbox afresh once the data plane has disabled it.</remarks>
  public Task<SandboxView> DisableAsync(string sandboxId, CancellationToken cancellationToken) =>
    PostAndReadAsync(SandboxPath(sandboxId) + "/disable", sandboxId, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Takes the sandbox afresh once the data plane has enabled it.</remarks>
  public Task<SandboxView> EnableAsync(string sandboxId, CancellationToken cancellationToken) =>
    PostAndReadAsync(SandboxPath(sandboxId) + "/enable", sandboxId, cancellationToken);

  /// <summary>Posts an empty body to <paramref name="path"/>, then reads the sandbox.</summary>
  private async Task<SandboxView> PostAndReadAsync(
    string path,
    string sandboxId,
    CancellationToken cancellationToken
  )
  {
    Call call = new(HttpMethod.Post, path, EmptyObject);
    using (var response = await SendAsync(call, cancellationToken))
    {
      await EnsureSuccessAsync(call, response, cancellationToken);
    }

    return await GetRequiredAsync(call, sandboxId, cancellationToken);
  }

  /// <summary>
  /// The IP access control that admits <paramref name="cidrs"/> alone: allow rules of at most
  /// <see cref="SandboxPortOptions.MaxCidrsPerRule"/> ranges, at priorities 10, 20, and so on, and
  /// deny by default. <see langword="null"/>, which leaves the port open to any address, for none.
  /// </summary>
  internal static IpAccessControlWire? ToIpAccessControl(IReadOnlyList<string> cidrs)
  {
    if (cidrs.Count == 0)
    {
      return null;
    }

    var chunks = cidrs.Chunk(SandboxPortOptions.MaxCidrsPerRule).ToArray();
    return new IpAccessControlWire
    {
      DefaultAction = "Deny",
      Rules =
      [
        .. chunks.Select(
          (chunk, index) =>
            new IpAccessRuleWire
            {
              Name =
                chunks.Length == 1
                  ? "gateway"
                  : "gateway-" + (index + 1).ToString(CultureInfo.InvariantCulture),
              Action = "Allow",
              Priority = (index + 1) * 10,
              SourceCidrs = chunk,
            }
        ),
      ],
    };
  }

  /// <summary>
  /// Whether <paramref name="exposed"/> is port <paramref name="port"/> as
  /// <paramref name="options"/> ask. Source ranges are compared only when the data plane reports
  /// some, since whether its answers carry them has not been observed.
  /// </summary>
  private static bool IsExposedAs(SandboxPort exposed, int port, SandboxPortOptions options) =>
    exposed.Port == port
    && exposed.Anonymous == options.Anonymous
    && exposed.Activation == options.Activation
    && (
      exposed.AllowedSourceCidrs.Count == 0
      || exposed
        .AllowedSourceCidrs.Order(StringComparer.Ordinal)
        .SequenceEqual(options.AllowedSourceCidrs.Order(StringComparer.Ordinal))
    );

  /// <summary>
  /// The ranges an IP access control that denies by default allows, as a request writes it
  /// (<see cref="IpAccessControlWire"/>); <see langword="null"/> for none, or any other shape.
  /// </summary>
  private static List<string>? AllowedSourceCidrs(JsonElement? accessControl)
  {
    if (
      accessControl is not { ValueKind: JsonValueKind.Object } control
      || !control.TryGetProperty("defaultAction", out var defaultAction)
      || defaultAction.ValueKind != JsonValueKind.String
      || !string.Equals(defaultAction.GetString(), "Deny", StringComparison.OrdinalIgnoreCase)
      || !control.TryGetProperty("rules", out var rules)
      || rules.ValueKind != JsonValueKind.Array
    )
    {
      return null;
    }

    List<string> cidrs = [];
    foreach (var rule in rules.EnumerateArray())
    {
      if (
        rule.ValueKind == JsonValueKind.Object
        && rule.TryGetProperty("action", out var action)
        && action.ValueKind == JsonValueKind.String
        && string.Equals(action.GetString(), "Allow", StringComparison.OrdinalIgnoreCase)
        && rule.TryGetProperty("sourceCidrs", out var sources)
        && sources.ValueKind == JsonValueKind.Array
      )
      {
        cidrs.AddRange(
          sources
            .EnumerateArray()
            .Where(source => source.ValueKind == JsonValueKind.String)
            .Select(source => source.GetString()!)
        );
      }
    }

    return cidrs.Count == 0 ? null : cidrs;
  }

  private static CreateSandboxRequest ToRequest(SandboxSpec spec)
  {
    AutoSuspendPolicyWire autoSuspend = new() { Enabled = false };
    if (spec.AutoSuspendAfter is { } idle)
    {
      ArgumentOutOfRangeException.ThrowIfLessThan(idle, TimeSpan.FromSeconds(1), nameof(spec));
      autoSuspend = new AutoSuspendPolicyWire
      {
        Enabled = true,
        Interval = checked((int)Math.Ceiling(idle.TotalSeconds)),
        // Suspends with the memory snapshot, so the renderer resumes with its browser warm.
        Mode = "Memory",
      };
    }

    return new CreateSandboxRequest
    {
      SourcesRef = new SourcesRefWire
      {
        DiskImage = new DiskImageWire { Id = spec.DiskImageId, IsPublic = false },
      },
      Resources = new ResourcesWire { Cpu = spec.Cpu, Memory = spec.Memory },
      EgressPolicy = new EgressPolicyWire { DefaultAction = spec.EgressDefaultAction },
      Entrypoint = spec.Entrypoint,
      Environment = spec.Environment,
      Labels = spec.Labels,
      Lifecycle = new LifecycleWire { AutoSuspendPolicy = autoSuspend },
      CustomerVnetConnectionName = spec.NetworkConnectionName,
    };
  }

  private static string SandboxPath(
    string sandboxId,
    [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(sandboxId))]
      string? parameterName = null
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId, parameterName);
    // Sandbox IDs are UUIDs. Anything but letters, digits, and hyphens ("..", "/", "?") could turn
    // the request into one for another path.
    if (!sandboxId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
    {
      throw new ArgumentException("A sandbox ID is letters, digits, and hyphens.", parameterName);
    }

    return "sandboxes/" + sandboxId;
  }

  /// <summary>
  /// The data plane refuses with <c>409</c> to stop a stopped sandbox (<c>SandboxNotRunning</c>), to
  /// resume a running one (<c>InvalidSandboxState</c>), and to add a port it already exposes
  /// (<c>PortAlreadyExists</c>), as on a retry after an answer that was lost. It also refuses while
  /// the sandbox is between states, such as <c>Stopping</c> on its way to <c>Stopped</c>, or while a
  /// port it already has waits for its address.
  /// </summary>
  /// <remarks>
  /// So on a <c>409</c> the sandbox is read until it is <paramref name="settled"/>, every
  /// <see cref="SettlePollInterval"/> for up to <see cref="SettleTimeout"/>. When it is
  /// <paramref name="done"/>, the call succeeds with it. When it settled only after a transition,
  /// the call may be made once more. Otherwise the refusal stands.
  /// </remarks>
  private async Task<ConflictOutcome> SettleConflictAsync(
    HttpResponseMessage response,
    string sandboxId,
    Func<SandboxView, bool> done,
    Func<SandboxResponse, bool> settled,
    CancellationToken cancellationToken
  )
  {
    if (response.StatusCode != HttpStatusCode.Conflict)
    {
      return default;
    }

    Call get = new(HttpMethod.Get, SandboxPath(sandboxId));
    var started = _time.GetTimestamp();
    for (var transitioned = false; ; transitioned = true)
    {
      if (await GetResponseAsync(get, cancellationToken) is not { } sandbox)
      {
        return default;
      }

      var view = ToView(get, sandbox);
      if (done(view))
      {
        return new ConflictOutcome(view, TryAgain: false);
      }

      if (settled(sandbox))
      {
        return new ConflictOutcome(null, TryAgain: transitioned);
      }

      if (_time.GetElapsedTime(started) >= SettleTimeout)
      {
        return default;
      }

      await Task.Delay(SettlePollInterval, _time, cancellationToken);
    }
  }

  /// <summary>Whether the sandbox is in a state it stays in: running or stopped.</summary>
  private static bool IsSettled(SandboxResponse sandbox) =>
    sandbox.State is SandboxStates.Running or SandboxStates.Stopped;

  /// <summary>Reads the sandbox as the data plane sends it, or <see langword="null"/> when it does not exist.</summary>
  private async Task<SandboxResponse?> GetResponseAsync(
    Call call,
    CancellationToken cancellationToken
  )
  {
    using var response = await SendAsync(call, cancellationToken);
    if (response.StatusCode == HttpStatusCode.NotFound)
    {
      return null;
    }

    await EnsureSuccessAsync(call, response, cancellationToken);
    return await ReadAsync(
      call,
      response,
      SandboxesJsonContext.Default.SandboxResponse,
      cancellationToken
    );
  }

  private async Task<SandboxView> GetRequiredAsync(
    Call call,
    string sandboxId,
    CancellationToken cancellationToken
  ) =>
    await GetAsync(sandboxId, cancellationToken)
    ?? throw new SandboxesException(
      $"{call} succeeded, but the sandbox no longer exists.",
      HttpStatusCode.NotFound
    );

  /// <summary>Sends <paramref name="call"/>, retrying it as the remarks on the class describe.</summary>
  private Task<HttpResponseMessage> SendAsync(Call call, CancellationToken cancellationToken) =>
    SendAsync(
      call,
      new Uri(_groupUri, call.Path + "?api-version=" + SandboxesOptions.ApiVersion),
      cancellationToken
    );

  /// <summary>Sends <paramref name="call"/> to <paramref name="uri"/>, retrying it.</summary>
  private async Task<HttpResponseMessage> SendAsync(
    Call call,
    Uri uri,
    CancellationToken cancellationToken
  )
  {
    for (var attempt = 0; ; attempt++)
    {
      var mayRetry = call.Retry && attempt < MaxRetries;
      HttpResponseMessage response;
      try
      {
        response = await SendOnceAsync(call, uri, cancellationToken);
      }
      catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
      {
        if (!mayRetry)
        {
          throw new SandboxesException($"{call} failed: {exception.Message}", exception);
        }

        await Task.Delay(BackOff(attempt), _time, cancellationToken);
        continue;
      }

      if (!mayRetry || !IsTransient(response.StatusCode))
      {
        return response;
      }

      var delay = RetryDelay(response, attempt);
      response.Dispose();
      await Task.Delay(delay, _time, cancellationToken);
    }
  }

  private async Task<HttpResponseMessage> SendOnceAsync(
    Call call,
    Uri uri,
    CancellationToken cancellationToken
  )
  {
    var token = await GetTokenAsync(call, cancellationToken);
    using HttpRequestMessage request = new(call.Method, uri);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (call.Body is { } body)
    {
      request.Content = new ByteArrayContent(body);
      request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
      {
        CharSet = "utf-8",
      };
    }

    // Responses are small JSON documents; reading them whole frees the connection at once.
    return await _httpClient.SendAsync(
      request,
      HttpCompletionOption.ResponseContentRead,
      cancellationToken
    );
  }

  private async ValueTask<string> GetTokenAsync(Call call, CancellationToken cancellationToken)
  {
    try
    {
      return await _tokens.GetTokenAsync(cancellationToken);
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
      throw new SandboxesException(
        $"{call} could not get an access token for {SandboxesOptions.TokenScope}: {exception.Message}",
        exception
      );
    }
  }

  /// <summary>
  /// A failure another attempt may not meet: the request never got an answer, or
  /// <see cref="HttpClient.Timeout"/> elapsed. The caller's own cancellation is not one.
  /// </summary>
  private static bool IsTransportFailure(
    Exception exception,
    CancellationToken cancellationToken
  ) =>
    exception is HttpRequestException
    || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

  private static bool IsTransient(HttpStatusCode status) =>
    status
      is HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

  private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
  {
    TimeSpan? retryAfter = response.Headers.RetryAfter switch
    {
      { Delta: { } delta } => delta,
      { Date: { } date } => date - _time.GetUtcNow(),
      _ => null,
    };
    if (retryAfter is not { } wait)
    {
      return BackOff(attempt);
    }

    return wait < TimeSpan.Zero ? TimeSpan.Zero
      : wait > MaxRetryDelay ? MaxRetryDelay
      : wait;
  }

  private static TimeSpan BackOff(int attempt) => FirstBackOff * Math.Pow(2, attempt);

  private static async Task EnsureSuccessAsync(
    Call call,
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    if (response.IsSuccessStatusCode)
    {
      return;
    }

    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    // Scrubbed before it is cut short, so no cut leaves part of a secret behind.
    var description = Truncate(Redact(DescribeError(body), call.Secrets));
    var status = response.StatusCode;
    throw new SandboxesException(
      $"{call} failed with {(int)status} ({status})"
        + (description.Length > 0 ? ": " + description : "."),
      status
    );
  }

  /// <summary>
  /// The data plane's description of an error. It answers with RFC 9457 problem details
  /// (<c>{"title":"SandboxNotFound","detail":"..."}</c>), and for invalid requests adds
  /// <c>errors</c>, a list of messages per field. Other shapes (<c>{"error":"..."}</c> from the port
  /// proxy, <c>{"error":{"code","message"}}</c> from Azure Resource Manager) are read too, and
  /// anything else is taken as text.
  /// </summary>
  internal static string DescribeError(string body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return "";
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      var root = document.RootElement;
      if (root.ValueKind == JsonValueKind.Object)
      {
        List<string> parts = [];
        AddString(parts, root, "title");
        AddString(parts, root, "detail");
        AddString(parts, root, "message");
        if (root.TryGetProperty("error", out var error))
        {
          if (error.ValueKind == JsonValueKind.String)
          {
            parts.Add(error.GetString()!);
          }
          else if (error.ValueKind == JsonValueKind.Object)
          {
            AddString(parts, error, "code");
            AddString(parts, error, "message");
          }
        }

        List<string> fields = [];
        if (
          root.TryGetProperty("errors", out var errors)
          && errors.ValueKind == JsonValueKind.Object
        )
        {
          foreach (var field in errors.EnumerateObject())
          {
            if (field.Value.ValueKind != JsonValueKind.Array)
            {
              continue;
            }

            foreach (var message in field.Value.EnumerateArray())
            {
              if (message.ValueKind == JsonValueKind.String)
              {
                fields.Add($"{field.Name}: {message.GetString()}");
              }
            }
          }
        }

        if (parts.Count > 0 || fields.Count > 0)
        {
          var description = string.Join(": ", parts);
          return fields.Count == 0 ? description
            : parts.Count == 0 ? string.Join("; ", fields)
            : $"{description} ({string.Join("; ", fields)})";
        }
      }
    }
    catch (JsonException)
    {
      // Not JSON: the text itself is the description.
    }

    return body.Trim();
  }

  private static void AddString(List<string> parts, JsonElement element, string name)
  {
    if (
      element.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.String
      && value.GetString() is { Length: > 0 } text
    )
    {
      parts.Add(text);
    }
  }

  private static string Redact(string text, IEnumerable<string>? secrets)
  {
    if (secrets is null)
    {
      return text;
    }

    foreach (var secret in secrets)
    {
      if (secret is null || secret.Length < MinRedactedLength)
      {
        continue;
      }

      // As sent, and as a JSON string would escape it, should the error quote the request's JSON.
      text = text.Replace(secret, Redacted, StringComparison.Ordinal);
      var escaped = JsonEncodedText.Encode(secret).Value;
      if (escaped != secret)
      {
        text = text.Replace(escaped, Redacted, StringComparison.Ordinal);
      }
    }

    return text;
  }

  private static string Truncate(string text) =>
    text.Length <= MaxErrorLength ? text : string.Concat(text.AsSpan(0, MaxErrorLength), "…");

  private static async Task<SandboxView> ReadSandboxAsync(
    Call call,
    HttpResponseMessage response,
    CancellationToken cancellationToken
  ) =>
    ToView(
      call,
      await ReadAsync(
        call,
        response,
        SandboxesJsonContext.Default.SandboxResponse,
        cancellationToken
      )
    );

  private static async Task<T> ReadAsync<T>(
    Call call,
    HttpResponseMessage response,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
        ?? throw Unexpected(call);
    }
    catch (JsonException exception)
    {
      throw Unexpected(call, exception);
    }
  }

  private static SandboxView ToView(Call call, SandboxResponse sandbox)
  {
    if (string.IsNullOrEmpty(sandbox.Id) || string.IsNullOrEmpty(sandbox.State))
    {
      throw Unexpected(call);
    }

    List<SandboxPort> ports = [];
    foreach (var port in sandbox.Ports ?? [])
    {
      // A port the proxy has no address for yet cannot be called; it is left out.
      if (Uri.TryCreate(port.Url, UriKind.Absolute, out var url))
      {
        ports.Add(
          new SandboxPort(port.Port, url, port.Auth?.Anonymous ?? false)
          {
            Activation = string.Equals(
              port.ActivationMode,
              "OnDemand",
              StringComparison.OrdinalIgnoreCase
            )
              ? SandboxPortActivation.OnDemand
              : SandboxPortActivation.Manual,
            // The same empty list as the default, so equal ports compare equal.
            AllowedSourceCidrs =
              AllowedSourceCidrs(port.IpAccessControl) ?? (IReadOnlyList<string>)[],
          }
        );
      }
    }

    return new SandboxView
    {
      Id = sandbox.Id,
      State = sandbox.State,
      StoppedReason = sandbox.StateDetails?.StoppedReason,
      StoppedAt = ParseTime(sandbox.StateDetails?.StoppedAt),
      Labels = sandbox.Labels ?? [],
      Ports = ports,
      CreatedAt = ParseTime(sandbox.CreatedAt),
    };
  }

  /// <summary>
  /// A time the data plane sent, or <see langword="null"/> when it sent none or one this client
  /// cannot parse: an unknown time, not a sandbox that cannot be read.
  /// </summary>
  private static DateTimeOffset? ParseTime(string? value) =>
    DateTimeOffset.TryParse(
      value,
      CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal,
      out var time
    )
      ? time
      : null;

  private static SandboxesException Unexpected(Call call) =>
    new($"{call} answered with a sandbox this client cannot read.");

  private static SandboxesException Unexpected(Call call, JsonException exception) =>
    new($"{call} answered with a sandbox this client cannot read.", exception);

  /// <summary>
  /// One data-plane call. Its string form names the call for messages: the method and the path
  /// within the group, never the body.
  /// </summary>
  /// <param name="Method">The HTTP method.</param>
  /// <param name="Path">The path relative to the group's address.</param>
  /// <param name="Body">The JSON body, if any.</param>
  /// <param name="Retry">Whether the call may be retried.</param>
  /// <param name="Secrets">Values to scrub from error messages.</param>
  private sealed record Call(
    HttpMethod Method,
    string Path,
    byte[]? Body = null,
    bool Retry = true,
    IEnumerable<string>? Secrets = null
  )
  {
    public override string ToString() => $"Sandboxes {Method} {Path}";
  }

  /// <summary>What a <c>409</c> turned out to mean; the default is that the refusal stands.</summary>
  /// <param name="Done">The sandbox, already where the call would have taken it.</param>
  /// <param name="TryAgain">The sandbox settled after a transition, so the call may work now.</param>
  private readonly record struct ConflictOutcome(SandboxView? Done, bool TryAgain);
}
