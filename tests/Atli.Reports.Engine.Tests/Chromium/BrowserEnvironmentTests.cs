using System.Diagnostics;
using Atli.Reports.Engine.Chromium.Browser;

namespace Atli.Reports.Engine.Tests.Chromium;

public class BrowserEnvironmentTests
{
  [Test]
  public async Task Application_credentials_are_removed_from_the_child_environment()
  {
    ProcessStartInfo start = new();
    start.Environment["AZURE_CLIENT_SECRET"] = "do-not-inherit";
    start.Environment["IDENTITY_ENDPOINT"] = "http://localhost/identity";
    start.Environment["AWS_SECRET_ACCESS_KEY"] = "do-not-inherit";
    start.Environment["ReportsServer__Authentication__ApiKeys__0__SecretHash"] = "do-not-inherit";

    BrowserEnvironment.Configure(start, new ReportsEngineBrowserOptions());

    await Assert.That(start.Environment.ContainsKey("AZURE_CLIENT_SECRET")).IsFalse();
    await Assert.That(start.Environment.ContainsKey("IDENTITY_ENDPOINT")).IsFalse();
    await Assert.That(start.Environment.ContainsKey("AWS_SECRET_ACCESS_KEY")).IsFalse();
    await Assert
      .That(start.Environment.ContainsKey("ReportsServer__Authentication__ApiKeys__0__SecretHash"))
      .IsFalse();
    await Assert
      .That(start.Environment["PATH"])
      .IsEqualTo(Environment.GetEnvironmentVariable("PATH"));
  }

  [Test]
  public async Task An_operator_can_deliberately_add_a_required_platform_variable()
  {
    ReportsEngineBrowserOptions options = new();
    options.EnvironmentVariables.Add("ATLI_TEST_FONT_DIRECTORY", "/fonts");
    ProcessStartInfo start = new();

    BrowserEnvironment.Configure(start, options);

    await Assert.That(start.Environment["ATLI_TEST_FONT_DIRECTORY"]).IsEqualTo("/fonts");
  }
}
