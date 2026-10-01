using Atli.Reports.Engine.Conversion;

namespace Atli.Reports.Engine.Tests.Conversion;

public class SignalShimTests
{
  [Test]
  public async Task CreateScript_wraps_the_named_binding_in_a_zero_argument_function()
  {
    var script = SignalShim.CreateScript("pdfReady");

    await Assert
      .That(script)
      .IsEqualTo(
        "<script>(function(){var n=\"pdfReady\";var o=window[n];"
          + "if(typeof o==='function'){window[n]=function(){o('ready')}}})();</script>"
      );
  }

  [Test]
  public async Task CreateScript_escapes_names_that_could_break_out_of_the_string_or_script()
  {
    var script = SignalShim.CreateScript("a\"b'c\\d</script><img src=x>");

    // Only the shim's own closing tag remains, and the quote cannot end the string literal.
    await Assert
      .That(script.IndexOf("</script>", StringComparison.Ordinal))
      .IsEqualTo(script.Length - "</script>".Length);
    await Assert.That(script).DoesNotContain("a\"b");
    await Assert.That(script).DoesNotContain("<img");
  }

  [Test]
  public async Task Apply_prepends_the_shim_to_the_html()
  {
    const string html = "<p>Hello</p>";

    var result = SignalShim.Apply(html, "done");

    await Assert.That(result).IsEqualTo(SignalShim.CreateScript("done") + html);
  }
}
