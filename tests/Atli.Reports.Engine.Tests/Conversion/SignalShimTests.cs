using Atli.Reports.Engine.Conversion;

namespace Atli.Reports.Engine.Tests.Conversion;

public class SignalShimTests
{
  [Test]
  public async Task CreateScript_defines_a_zero_argument_function_that_calls_the_binding()
  {
    var script = SignalShim.CreateScript("pdfReady");

    await Assert
      .That(script)
      .IsEqualTo(
        "(function(){var n=\"pdfReady\";window[n]=function(){"
          + "var b=window[\"__atliReportsSignal\"];if(typeof b==='function'){b('ready')}}})();"
      );
  }

  [Test]
  public async Task CreateScript_escapes_names_that_could_break_out_of_the_string()
  {
    var script = SignalShim.CreateScript("a\"b'c\\d\n</script>");

    await Assert.That(script).DoesNotContain("a\"b");
    await Assert.That(script).DoesNotContain("\n");
    await Assert.That(script).DoesNotContain("</script>");
  }

  [Test]
  public async Task CreateScript_contains_no_markup()
  {
    // The script is registered with Page.addScriptToEvaluateOnNewDocument, never written into the
    // HTML, so it cannot push the document into quirks mode.
    await Assert.That(SignalShim.CreateScript("done")).DoesNotContain("<script");
  }
}
