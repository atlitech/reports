using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Tailwind;

namespace Atli.Reports.Blazor.Tests.Configuration;

public class TailwindOptionsTests
{
  [Test]
  [Arguments("Reports/Invoice", false)]
  [Arguments("Reports\\Invoice", true)]
  public async Task Report_bundles_resolve_from_the_application_directory(
    string bundlePath,
    bool reloadOnChange
  )
  {
    BlazorReportRegistrationOptions options = new();

    var configured = options.UseTailwind(bundlePath, reloadOnChange);

    await Assert.That(configured).IsSameReferenceAs(options);
    await Assert
      .That(options.BaseStylesPath)
      .IsEqualTo(Path.Combine(AppContext.BaseDirectory, "tailwind", "Reports", "Invoice.css"));
    await Assert.That(options.BaseStylesReloadOnChange).IsEqualTo(reloadOnChange);
  }

  [Test]
  public async Task The_global_helper_selects_the_bundle_and_defaults_to_cached_styles()
  {
    BlazorReportOptions options = new() { BaseStylesReloadOnChange = true };

    var configured = options.UseTailwind("shared");

    await Assert.That(configured).IsSameReferenceAs(options);
    await Assert
      .That(options.BaseStylesPath)
      .IsEqualTo(Path.Combine(AppContext.BaseDirectory, "tailwind", "shared.css"));
    await Assert.That(options.BaseStylesReloadOnChange).IsFalse();
  }

  [Test]
  [Arguments("")]
  [Arguments(" ")]
  [Arguments("../Invoice")]
  [Arguments("Reports/../Invoice")]
  [Arguments("Reports\\..\\Invoice")]
  [Arguments("/Invoice")]
  [Arguments("C:\\Reports\\Invoice")]
  [Arguments("//server/Invoice")]
  [Arguments("Reports//Invoice")]
  [Arguments("./Invoice")]
  [Arguments("Reports/")]
  [Arguments("Invoice.css")]
  [Arguments("Invoice.tailwind.css")]
  [Arguments("Invoice.CSS")]
  [Arguments("Reports/Invoice?")]
  [Arguments("Reports/Inv*oice")]
  [Arguments("Reports/Inv\"oice")]
  [Arguments("Reports/Inv<oice")]
  [Arguments("Reports/Inv>oice")]
  [Arguments("Reports/Inv|oice")]
  [Arguments("Reports/Invoice\n")]
  [Arguments("Reports/Invoice.")]
  [Arguments("Reports/Invoice ")]
  [Arguments("CON")]
  [Arguments("Reports/nul")]
  [Arguments("Reports/AUX.theme")]
  [Arguments("Reports/COM1")]
  [Arguments("LPT9/Invoice")]
  public async Task Invalid_bundle_paths_are_rejected_consistently_by_both_helpers(string path)
  {
    BlazorReportRegistrationOptions reportOptions = new();
    BlazorReportOptions globalOptions = new();

    await Assert.That(() => reportOptions.UseTailwind(path)).Throws<ArgumentException>();
    await Assert.That(() => globalOptions.UseTailwind(path)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Null_options_or_bundle_paths_are_rejected()
  {
    await Assert
      .That(() => ((BlazorReportRegistrationOptions)null!).UseTailwind("Invoice"))
      .Throws<ArgumentNullException>();
    await Assert
      .That(() => ((BlazorReportOptions)null!).UseTailwind("Invoice"))
      .Throws<ArgumentNullException>();
    await Assert
      .That(() => new BlazorReportRegistrationOptions().UseTailwind(null!))
      .Throws<ArgumentNullException>();
    await Assert
      .That(() => new BlazorReportOptions().UseTailwind(null!))
      .Throws<ArgumentNullException>();
  }
}
