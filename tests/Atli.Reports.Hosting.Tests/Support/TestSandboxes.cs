using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>
/// A <see cref="SandboxesClient"/> over a <see cref="FakeDataPlane"/> that answers with
/// <c>answers</c>, with its tokens from a <see cref="FakeCredential"/> and its retry delays and token
/// expiry on a <see cref="TestClock"/>.
/// </summary>
internal sealed class TestSandboxes : IDisposable
{
  private readonly HttpClient _http;

  public TestSandboxes(params Func<ReceivedRequest, HttpResponseMessage>[] answers)
  {
    Clock = new TestClock();
    Plane = new FakeDataPlane(answers);
    Credential = new FakeCredential(Clock);
    _http = new HttpClient(Plane);
    Client = new SandboxesClient(_http, Credential, FakeDataPlane.Options, Clock);
  }

  public SandboxesClient Client { get; }

  public FakeDataPlane Plane { get; }

  public FakeCredential Credential { get; }

  public TestClock Clock { get; }

  public void Dispose() => _http.Dispose();
}
