using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;

namespace Atli.Reports.Hosting.Tests.Sandboxes;

/// <summary>
/// What a failed call throws: the status and the data plane's own description, and never a
/// request's body or headers, which for a create carry the renderer's credentials.
/// </summary>
public class SandboxesClientErrorTests
{
  private const string Id = "98c01b65-b81b-4dca-b000-fdae0eb0939c";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_failure_carries_the_status_and_the_problem_title_and_detail()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.BadRequest,
        "DiskImageNotFound",
        "Disk image 'disk-1' not found in user disk images"
      )
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.CreateAsync(Spec(), TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert
      .That(exception.Message)
      .IsEqualTo(
        "Sandboxes PUT sandboxes failed with 400 (BadRequest): DiskImageNotFound: "
          + "Disk image 'disk-1' not found in user disk images"
      );
  }

  [Test]
  public async Task A_validation_failure_lists_its_fields()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Json(
        HttpStatusCode.BadRequest,
        """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "One or more validation errors occurred.",
          "status": 400,
          "errors": { "SourcesRef": ["'sourcesRef' is required when not using a preset sandbox type"] }
        }
        """,
        "application/problem+json"
      )
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.CreateAsync(Spec(), TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .EndsWith(
        ": One or more validation errors occurred. "
          + "(SourcesRef: 'sourcesRef' is required when not using a preset sandbox type)"
      );
  }

  [Test]
  [Arguments("""{"error":"Sandbox is not running"}""", ": Sandbox is not running")]
  [Arguments(
    """{"error":{"code":"AuthorizationFailed","message":"No access."}}""",
    ": AuthorizationFailed: No access."
  )]
  [Arguments("upstream connect error", ": upstream connect error")]
  [Arguments("", ".")]
  public async Task Other_error_shapes_are_described_too(string body, string ending)
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Json(HttpStatusCode.Forbidden, body, "text/plain")
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.ResumeAsync(Id, TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    await Assert
      .That(exception.Message)
      .IsEqualTo($"Sandboxes POST sandboxes/{Id}/resume failed with 403 (Forbidden){ending}");
  }

  [Test]
  public async Task A_long_description_is_cut_short()
  {
    var detail = new string('x', 5000);
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.InternalServerError, "Boom", detail)
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.GetAsync(Id, TestToken))
      .Throws<SandboxesException>();

    var prefix = $"Sandboxes GET sandboxes/{Id} failed with 500 (InternalServerError): Boom: ";
    await Assert.That(exception!.Message).StartsWith(prefix);
    await Assert
      .That(exception.Message.Length)
      .IsEqualTo(prefix.Length - "Boom: ".Length + SandboxesClient.MaxErrorLength + 1);
    await Assert.That(exception.Message).EndsWith("x…");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task A_failed_create_never_carries_the_environment_even_when_the_data_plane_echoes_it(
    bool asProblemDetails
  )
  {
    var credential = RendererCredential.Generate();
    var environment = RendererServerEnvironment.Create(credential, RendererSize.Medium);
    using TestSandboxes sandboxes = new(request =>
    // A data plane that quotes the whole request back: in problem details, or as plain text.
    new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
      Content = asProblemDetails
        ? new StringContent(
          new JsonObject
          {
            ["title"] = "Bad",
            ["detail"] = request.Body,
            ["errors"] = new JsonObject { ["$"] = new JsonArray(request.Body) },
          }.ToJsonString(),
          Encoding.UTF8,
          "application/problem+json"
        )
        : new StringContent(request.Body!, Encoding.UTF8, "text/plain"),
    });

    var exception = await Assert
      .That(async () =>
        await sandboxes.Client.CreateAsync(Spec() with { Environment = environment }, TestToken)
      )
      .Throws<SandboxesException>();

    var text = exception!.ToString();
    await Assert.That(text).Contains("[redacted]");
    await Assert.That(text).DoesNotContain(credential.Verifier);
    await Assert.That(text).DoesNotContain(JsonEncodedText.Encode(credential.Verifier).Value);
    await Assert.That(text).DoesNotContain(credential.KeyId);
    await Assert.That(text).DoesNotContain(credential.Credential);
    await Assert.That(text).DoesNotContain("Bearer");
    await Assert.That(text).DoesNotContain("token-1");
  }

  [Test]
  public async Task A_transport_failure_on_create_names_the_call_but_not_its_body()
  {
    var credential = RendererCredential.Generate();
    using TestSandboxes sandboxes = new(FakeDataPlane.Unreachable());

    var exception = await Assert
      .That(async () =>
        await sandboxes.Client.CreateAsync(
          Spec() with
          {
            Environment = RendererServerEnvironment.Create(credential, RendererSize.Small),
          },
          TestToken
        )
      )
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsNull();
    await Assert.That(exception.InnerException).IsTypeOf<HttpRequestException>();
    await Assert
      .That(exception.Message)
      .IsEqualTo("Sandboxes PUT sandboxes failed: Connection refused");
    await Assert.That(exception.ToString()).DoesNotContain(credential.Verifier);
  }

  [Test]
  public async Task A_response_the_client_cannot_read_fails_as_such()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("""{"id":"x"}"""));

    var exception = await Assert
      .That(async () => await sandboxes.Client.GetAsync(Id, TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo($"Sandboxes GET sandboxes/{Id} answered with a sandbox this client cannot read.");
  }

  [Test]
  public async Task Malformed_json_fails_as_unreadable()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("<html>"));

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.InnerException).IsTypeOf<JsonException>();
  }

  [Test]
  public async Task A_credential_that_fails_fails_the_call_without_sending_it()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    sandboxes.Credential.Failure = new InvalidOperationException("No managed identity endpoint.");

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "Sandboxes GET sandboxes could not get an access token for "
          + "https://dynamicsessions.io/.default: No managed identity endpoint."
      );
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task The_callers_cancellation_is_not_a_sandboxes_failure()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    using CancellationTokenSource canceled = new();
    await canceled.CancelAsync();

    await Assert
      .That(async () => await sandboxes.Client.ListAsync(canceled.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Describes_problem_details_and_other_bodies()
  {
    await Assert
      .That(SandboxesClient.DescribeError("""{"title":"T","detail":"D","errorCode":1}"""))
      .IsEqualTo("T: D");
    await Assert.That(SandboxesClient.DescribeError("""{"message":"M"}""")).IsEqualTo("M");
    await Assert
      .That(SandboxesClient.DescribeError("""{"errors":{"a":["x","y"],"b":["z"]}}"""))
      .IsEqualTo("a: x; a: y; b: z");
    await Assert.That(SandboxesClient.DescribeError("[1,2]")).IsEqualTo("[1,2]");
    await Assert.That(SandboxesClient.DescribeError("  plain  ")).IsEqualTo("plain");
    await Assert.That(SandboxesClient.DescribeError(" ")).IsEqualTo("");
  }

  private static SandboxSpec Spec() =>
    new()
    {
      DiskImageId = "disk-1",
      Cpu = "500m",
      Memory = "1024Mi",
      Entrypoint = ["/bin/sleep", "infinity"],
    };
}
