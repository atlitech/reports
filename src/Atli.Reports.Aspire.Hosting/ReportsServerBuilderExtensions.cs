using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Adds the Atli Reports server to an Aspire application model and configures it.
/// </summary>
public static partial class ReportsServerBuilderExtensions
{
  /// <summary>
  /// The port the server listens on inside its container.
  /// </summary>
  private const int ServerPort = 8080;

  /// <summary>
  /// The server's readiness probe: its browser can launch and recent conversions mostly succeed.
  /// </summary>
  private const string ReadinessPath = "/health/ready";

  /// <summary>
  /// The server's liveness probe: the server answers at all. It runs no engine check, because a
  /// restart does not repair a browser that cannot start.
  /// </summary>
  private const string LivenessPath = "/health/live";

  /// <summary>
  /// The compute environments that publish to Kubernetes. Their publisher writes a probe's scheme in
  /// lower case, which Kubernetes rejects, so a pod with a probe is never created
  /// (https://github.com/microsoft/aspire/issues/18271); the server gets no probes there until
  /// Aspire fixes it (https://github.com/atlitech/reports/issues/147).
  /// </summary>
  private static readonly string[] KubernetesEnvironmentTypes =
  [
    "Aspire.Hosting.Kubernetes.KubernetesEnvironmentResource",
    "Aspire.Hosting.Azure.Kubernetes.AzureKubernetesEnvironmentResource",
  ];

  /// <summary>
  /// The server's OpenAPI document, which describes <c>POST /convert</c>.
  /// </summary>
  private const string OpenApiDocumentPath = "/openapi/v1.json";

  private const string MaxConcurrentConversionsVariable =
    "ReportsEngine__Concurrency__MaxConcurrentConversions";
  private const string MaxQueueLengthVariable = "ReportsEngine__Concurrency__MaxQueueLength";
  private const string QueueTimeoutVariable = "ReportsEngine__Concurrency__QueueTimeout";
  private const string ConversionTimeoutVariable = "ReportsEngine__ConversionTimeout";
  private const string MaxConversionsPerProcessVariable =
    "ReportsEngine__Browser__MaxConversionsPerProcess";
  private const string MaxProcessLifetimeVariable = "ReportsEngine__Browser__MaxProcessLifetime";

  /// <summary>
  /// Adds an Atli Reports server to the application model: the
  /// <c>ghcr.io/atlitech/reports-server</c> container, tagged with this package's version.
  /// </summary>
  /// <param name="builder">The application builder.</param>
  /// <param name="name">The name of the resource, which is also the name of its connection string.</param>
  /// <param name="port">
  /// The host port of the server's HTTP endpoint. <see langword="null"/> (the default) picks a free
  /// port each time the app starts.
  /// </param>
  /// <returns>The builder of the new <see cref="ReportsServerResource"/>.</returns>
  /// <remarks>
  /// <para>
  /// The server listens on port 8080 in the container, behind the <c>http</c> endpoint. The resource
  /// turns healthy once <c>/health/ready</c> answers <c>200</c>, and the server answers <c>503</c>
  /// while its browser fails to launch (the image launches it at startup, and the server retries in
  /// the background), so <c>WaitFor</c> waits for a server that can convert. The server sends its
  /// logs, metrics, and traces to the dashboard over OTLP, the dashboard links the server's OpenAPI
  /// document (<c>/openapi/v1.json</c>), and it offers a <c>Convert a test page</c> command that
  /// checks the whole path end to end.
  /// </para>
  /// <para>
  /// Deployment targets that run probes, such as Azure Container Apps, probe <c>/health/ready</c>
  /// for readiness and <c>/health/live</c> for liveness. Kubernetes gets no probes until Aspire's
  /// Kubernetes publisher writes ones Kubernetes accepts
  /// (https://github.com/microsoft/aspire/issues/18271). <c>WithHttpProbe</c> replaces a probe of
  /// the same type, but not on <c>/health/ready</c>: that would add a second health check on the
  /// path, and Aspire rejects the duplicate.
  /// </para>
  /// <para>
  /// The server's browser runs with Chromium's own sandbox. Locally, the container runs with the
  /// seccomp profile the sandbox needs (Docker's default plus user namespaces), which the package
  /// carries and passes to Docker or Podman as <c>--security-opt seccomp=&lt;file&gt;</c>. Deployment
  /// targets do not get that argument: give the target the profile
  /// (<c>deploy/seccomp/chromium.json</c> in the repository), or, for trusted HTML on a target that
  /// cannot apply one, such as Azure Container Apps, opt out explicitly with
  /// <c>.WithEnvironment("ReportsEngine__Browser__NoSandbox", "true")</c>. Without either, the server
  /// stays unready and its health details say why.
  /// </para>
  /// <para>
  /// Select authentication explicitly: <c>WithDevelopmentApiKey()</c> for a local run,
  /// <c>WithApiKeyAuthentication</c> for secret deployment parameters, or configure the server's
  /// JWT bearer settings through <c>WithEnvironment</c>. The server refuses to start without an
  /// authentication mode. <c>WithAnonymousAccess()</c> explicitly opts out of caller authentication.
  /// </para>
  /// <para>
  /// Reference the server from an app with <c>WithReference</c>; the app reads the connection string
  /// with <c>builder.AddReportsClient(name)</c> from <c>Atli.Reports.Client</c>. Configure the server
  /// with <see cref="WithMaxConcurrentConversions"/>, <see cref="WithMaxQueueLength"/>,
  /// <see cref="WithQueueTimeout"/>, <see cref="WithConversionTimeout"/>, and
  /// <see cref="WithBrowserRecycling"/>, or set any other <c>ReportsEngine__*</c> variable with
  /// <c>WithEnvironment</c>.
  /// </para>
  /// <para>
  /// <c>WithImageTag</c> runs another release of the server. <c>WithDockerfile</c> builds the server
  /// from a clone of the repository instead of pulling it, for example
  /// <c>.WithDockerfile("../reports", "src/Atli.Reports.Server/Dockerfile")</c>.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// var reports = builder.AddReportsServer("reports").WithDevelopmentApiKey().WithMaxConcurrentConversions(4);
  ///
  /// builder.AddProject&lt;Projects.Api&gt;("api").WithReference(reports).WaitFor(reports);
  /// </code>
  /// </example>
  public static IResourceBuilder<ReportsServerResource> AddReportsServer(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    int? port = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrEmpty(name);

    ReportsServerResource resource = new(name);
    return builder
      .AddResource(resource)
      .WithImage(ReportsServerContainerImageTags.Image, ReportsServerContainerImageTags.Tag)
      .WithImageRegistry(ReportsServerContainerImageTags.Registry)
      .WithHttpEndpoint(
        port: port,
        targetPort: ServerPort,
        name: ReportsServerResource.HttpEndpointName
      )
      .WithHttpHealthCheck(ReadinessPath, endpointName: ReportsServerResource.HttpEndpointName)
      .WithUrlForEndpoint(
        ReportsServerResource.HttpEndpointName,
        _ => new() { Url = OpenApiDocumentPath, DisplayText = "OpenAPI document" }
      )
      .WithOtlpExporter()
      .WithIconName("DocumentPdf")
      .WithConvertTestPageCommand()
      .WithChromiumSeccompProfile()
      .WithHealthProbes();
  }

  /// <summary>
  /// Adds the readiness and liveness probes that deployment targets run, except on Kubernetes (see
  /// <see cref="KubernetesEnvironmentTypes"/>). <c>WithHttpProbe</c> replaces a probe of the same
  /// type.
  /// </summary>
  private static IResourceBuilder<ReportsServerResource> WithHealthProbes(
    this IResourceBuilder<ReportsServerResource> builder
  )
  {
    var endpoint = builder.GetEndpoint(ReportsServerResource.HttpEndpointName);

    // The annotations rather than WithHttpProbe, which would also add a second health check on
    // /health/ready, and Aspire rejects a duplicate.
#pragma warning disable ASPIREPROBES001 // Probes are experimental in Aspire 13.
    EndpointProbeAnnotation[] probes =
    [
      new()
      {
        Type = ProbeType.Readiness,
        EndpointReference = endpoint,
        Path = ReadinessPath,
        InitialDelaySeconds = 5,
        PeriodSeconds = 5,
        TimeoutSeconds = 3,
        FailureThreshold = 3,
        SuccessThreshold = 1,
      },
      // Restarts the server only after about 30 seconds without an answer: a restart drops the
      // conversions in flight, and a busy browser can hold the container's CPU for a while.
      new()
      {
        Type = ProbeType.Liveness,
        EndpointReference = endpoint,
        Path = LivenessPath,
        InitialDelaySeconds = 5,
        PeriodSeconds = 10,
        TimeoutSeconds = 5,
        FailureThreshold = 3,
        SuccessThreshold = 1,
      },
    ];
    foreach (var probe in probes)
    {
      builder.WithAnnotation(probe);
    }

    // The environments are known once the model is complete. This runs before the publishers read
    // the probes: Aspire 13.0's Kubernetes publisher reads them in a BeforeStartEvent handler it
    // subscribes when the app starts, after this one; later releases read them in a pipeline step.
    builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>(
      (@event, _) =>
      {
        if (@event.Model.Resources.Any(IsKubernetesEnvironment))
        {
          foreach (var probe in probes)
          {
            builder.Resource.Annotations.Remove(probe);
          }
        }

        return Task.CompletedTask;
      }
    );
#pragma warning restore ASPIREPROBES001

    return builder;
  }

  private static bool IsKubernetesEnvironment(IResource resource)
  {
    for (var type = resource.GetType(); type is not null; type = type.BaseType)
    {
      if (KubernetesEnvironmentTypes.Contains(type.FullName))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Sets the most conversions the server renders at the same time
  /// (<c>ReportsEngine:Concurrency:MaxConcurrentConversions</c>).
  /// </summary>
  /// <param name="builder">The server's builder.</param>
  /// <param name="maxConcurrentConversions">
  /// The number of conversions; at least 1. The server's default is the number of processors, but
  /// at least 2 and at most 8.
  /// </param>
  /// <returns>The <paramref name="builder"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrentConversions"/> is less than 1.</exception>
  /// <remarks>
  /// Every conversion renders in the same browser process, so throughput stops growing beyond a
  /// handful of concurrent conversions. Conversions beyond this limit wait in the queue.
  /// </remarks>
  public static IResourceBuilder<ReportsServerResource> WithMaxConcurrentConversions(
    this IResourceBuilder<ReportsServerResource> builder,
    int maxConcurrentConversions
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentConversions, 1);

    return builder.WithEnvironment(
      MaxConcurrentConversionsVariable,
      Format(maxConcurrentConversions)
    );
  }

  /// <summary>
  /// Sets the most conversions that may wait for a turn
  /// (<c>ReportsEngine:Concurrency:MaxQueueLength</c>).
  /// </summary>
  /// <param name="builder">The server's builder.</param>
  /// <param name="maxQueueLength">
  /// The number of waiting conversions; <c>0</c> answers <c>503 Service Unavailable</c> as soon as
  /// every slot is taken. The server's default is 100.
  /// </param>
  /// <returns>The <paramref name="builder"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxQueueLength"/> is negative.</exception>
  public static IResourceBuilder<ReportsServerResource> WithMaxQueueLength(
    this IResourceBuilder<ReportsServerResource> builder,
    int maxQueueLength
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentOutOfRangeException.ThrowIfNegative(maxQueueLength);

    return builder.WithEnvironment(MaxQueueLengthVariable, Format(maxQueueLength));
  }

  /// <summary>
  /// Sets the longest a conversion waits for a turn before the server answers
  /// <c>503 Service Unavailable</c> (<c>ReportsEngine:Concurrency:QueueTimeout</c>).
  /// </summary>
  /// <param name="builder">The server's builder.</param>
  /// <param name="queueTimeout">
  /// The wait; not negative, or <see cref="Timeout.InfiniteTimeSpan"/> to wait until the request is
  /// canceled. The server's default is 30 seconds.
  /// </param>
  /// <returns>The <paramref name="builder"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="queueTimeout"/> is negative and not infinite.</exception>
  public static IResourceBuilder<ReportsServerResource> WithQueueTimeout(
    this IResourceBuilder<ReportsServerResource> builder,
    TimeSpan queueTimeout
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    if (queueTimeout < TimeSpan.Zero && queueTimeout != Timeout.InfiniteTimeSpan)
    {
      throw new ArgumentOutOfRangeException(
        nameof(queueTimeout),
        queueTimeout,
        "The queue timeout must not be negative, except Timeout.InfiniteTimeSpan."
      );
    }

    return builder.WithEnvironment(QueueTimeoutVariable, Format(queueTimeout));
  }

  /// <summary>
  /// Sets the longest one conversion may take as a whole, its wait in the queue included, before
  /// the server answers <c>504 Gateway Timeout</c> (<c>ReportsEngine:ConversionTimeout</c>).
  /// </summary>
  /// <param name="builder">The server's builder.</param>
  /// <param name="conversionTimeout">
  /// The limit; greater than zero, or <see cref="Timeout.InfiniteTimeSpan"/> for none. The server's
  /// default is one minute.
  /// </param>
  /// <returns>The <paramref name="builder"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="conversionTimeout"/> is not greater than zero and not infinite.</exception>
  /// <remarks>
  /// Keep it below the timeouts of the server's clients, so overload ends in the server's clean error
  /// rather than a dropped request. <c>Atli.Reports.Client</c> waits up to two minutes per attempt by
  /// default; raise its <c>AttemptTimeout</c> along with this limit.
  /// </remarks>
  public static IResourceBuilder<ReportsServerResource> WithConversionTimeout(
    this IResourceBuilder<ReportsServerResource> builder,
    TimeSpan conversionTimeout
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ThrowIfNotPositiveOrInfinite(conversionTimeout, nameof(conversionTimeout));

    return builder.WithEnvironment(ConversionTimeoutVariable, Format(conversionTimeout));
  }

  /// <summary>
  /// Sets when the server replaces its browser process with a fresh one, which bounds the memory a
  /// long-running browser accumulates (<c>ReportsEngine:Browser:MaxConversionsPerProcess</c> and
  /// <c>ReportsEngine:Browser:MaxProcessLifetime</c>).
  /// </summary>
  /// <param name="builder">The server's builder.</param>
  /// <param name="maxConversionsPerProcess">
  /// How many conversions one browser process serves; <c>0</c> never replaces it for this reason.
  /// <see langword="null"/> keeps the server's default, 1000.
  /// </param>
  /// <param name="maxProcessLifetime">
  /// How long one browser process is used; greater than zero, or
  /// <see cref="Timeout.InfiniteTimeSpan"/> never to replace it for this reason.
  /// <see langword="null"/> keeps the server's default, one hour.
  /// </param>
  /// <returns>The <paramref name="builder"/>.</returns>
  /// <exception cref="ArgumentException">Neither limit is set.</exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// <paramref name="maxConversionsPerProcess"/> is negative, or <paramref name="maxProcessLifetime"/>
  /// is not greater than zero and not infinite.
  /// </exception>
  /// <remarks>
  /// The replacement starts at once; conversions already running finish on the old process. To keep
  /// one browser process for the server's whole life, pass <c>0</c> and
  /// <see cref="Timeout.InfiniteTimeSpan"/>.
  /// </remarks>
  public static IResourceBuilder<ReportsServerResource> WithBrowserRecycling(
    this IResourceBuilder<ReportsServerResource> builder,
    int? maxConversionsPerProcess = null,
    TimeSpan? maxProcessLifetime = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    if (maxConversionsPerProcess is null && maxProcessLifetime is null)
    {
      throw new ArgumentException(
        $"Set {nameof(maxConversionsPerProcess)}, {nameof(maxProcessLifetime)}, or both."
      );
    }

    // Both limits are checked before either is set, so a rejected call sets nothing.
    List<(string Name, string Value)> variables = [];
    if (maxConversionsPerProcess is { } conversions)
    {
      ArgumentOutOfRangeException.ThrowIfNegative(conversions, nameof(maxConversionsPerProcess));
      variables.Add((MaxConversionsPerProcessVariable, Format(conversions)));
    }

    if (maxProcessLifetime is { } lifetime)
    {
      ThrowIfNotPositiveOrInfinite(lifetime, nameof(maxProcessLifetime));
      variables.Add((MaxProcessLifetimeVariable, Format(lifetime)));
    }

    foreach (var (name, value) in variables)
    {
      builder.WithEnvironment(name, value);
    }

    return builder;
  }

  private static void ThrowIfNotPositiveOrInfinite(TimeSpan value, string paramName)
  {
    if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
    {
      throw new ArgumentOutOfRangeException(
        paramName,
        value,
        "The value must be greater than zero, or Timeout.InfiniteTimeSpan."
      );
    }
  }

  private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

  // The constant format ("c") is what configuration binding parses back: 00:01:30, or
  // -00:00:00.0010000 for Timeout.InfiniteTimeSpan.
  private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);
}
