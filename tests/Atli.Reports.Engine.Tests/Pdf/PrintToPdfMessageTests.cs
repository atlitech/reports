using Atli.Reports.Engine.Pdf;

namespace Atli.Reports.Engine.Tests.Pdf;

public class PrintToPdfMessageTests
{
  [Test]
  public async Task Default_options_map_to_a_portrait_letter_page_streamed_back()
  {
    var message = ChromiumPdfGenerator.CreatePrintToPdfMessage(new PdfOptions());

    await Assert.That(message.Method).IsEqualTo("Page.printToPDF");
    await Assert.That((bool)message.Parameters["landscape"]).IsFalse();
    await Assert.That(message.Parameters["paperWidth"]).IsEqualTo(8.5);
    await Assert.That(message.Parameters["paperHeight"]).IsEqualTo(11d);
    await Assert.That(message.Parameters["marginTop"]).IsEqualTo(0.4);
    await Assert.That((bool)message.Parameters["printBackground"]).IsTrue();
    await Assert.That(message.Parameters["scale"]).IsEqualTo(1.0);
    await Assert.That(message.Parameters["transferMode"]).IsEqualTo("ReturnAsStream");
    await Assert.That((bool)message.Parameters["generateTaggedPDF"]).IsTrue();
    await Assert.That(message.Parameters.ContainsKey("headerTemplate")).IsFalse();
    await Assert.That(message.Parameters.ContainsKey("displayHeaderFooter")).IsFalse();
    await Assert.That(message.Parameters.ContainsKey("pageRanges")).IsFalse();
    await Assert.That(message.Parameters.ContainsKey("preferCSSPageSize")).IsFalse();
  }

  [Test]
  public async Task Every_option_maps_to_its_DevTools_parameter()
  {
    PdfOptions options = new()
    {
      Orientation = PageOrientation.Landscape,
      PaperSize = PaperSize.A4,
      Margins = Margins.None with { Top = 1 },
      PrintBackground = false,
      Scale = 0.5,
      HeaderTemplate = "<span class=\"title\"></span>",
      FooterTemplate = "<span class=\"pageNumber\"></span>",
      DisplayHeaderFooter = true,
      PageRanges = "1-2",
      PreferCssPageSize = true,
      GenerateTaggedPdf = false,
    };

    var parameters = ChromiumPdfGenerator.CreatePrintToPdfMessage(options).Parameters;

    await Assert.That((bool)parameters["landscape"]).IsTrue();
    await Assert.That(parameters["paperWidth"]).IsEqualTo(8.27);
    await Assert.That(parameters["paperHeight"]).IsEqualTo(11.69);
    await Assert.That(parameters["marginTop"]).IsEqualTo(1d);
    await Assert.That(parameters["marginBottom"]).IsEqualTo(0d);
    await Assert.That(parameters["marginLeft"]).IsEqualTo(0d);
    await Assert.That(parameters["marginRight"]).IsEqualTo(0d);
    await Assert.That((bool)parameters["printBackground"]).IsFalse();
    await Assert.That(parameters["scale"]).IsEqualTo(0.5);
    await Assert.That(parameters["headerTemplate"]).IsEqualTo("<span class=\"title\"></span>");
    await Assert.That(parameters["footerTemplate"]).IsEqualTo("<span class=\"pageNumber\"></span>");
    await Assert.That((bool)parameters["displayHeaderFooter"]).IsTrue();
    await Assert.That(parameters["pageRanges"]).IsEqualTo("1-2");
    await Assert.That((bool)parameters["preferCSSPageSize"]).IsTrue();
    await Assert.That((bool)parameters["generateTaggedPDF"]).IsFalse();
  }
}
