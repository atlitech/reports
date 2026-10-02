using System.Buffers.Binary;
using System.Text;
using Atli.Reports.Worker.Protocol;

namespace Atli.Reports.Engine.Tests.Workers;

public class WorkerProtocolTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;
  private static readonly Guid JobId = Guid.Parse("ac4e49fd-70a2-4df9-a3cb-f8713c0f3b6e");

  [Test]
  public async Task Requests_round_trip_large_unicode_documents_and_render_options()
  {
    using MemoryStream wire = new();
    var html = "<h1>Reporte 🦎</h1>" + new string('x', 100_000);
    var options = new PdfOptions
    {
      Orientation = PageOrientation.Landscape,
      PaperSize = PaperSize.A3,
      Margins = Margins.None,
      WaitForSignal = "ready",
      WaitTimeout = TimeSpan.FromSeconds(7),
    };
    await WorkerProtocol.WriteRequestAsync(
      wire,
      new WorkerRequest(1, JobId, html, options),
      TestToken
    );
    wire.Position = 0;
    var request = await WorkerProtocol.ReadRequestAsync(wire, TestToken);
    await Assert.That(request.JobId).IsEqualTo(JobId);
    await Assert.That(request.Html).IsEqualTo(html);
    await Assert.That(request.Options!.Orientation).IsEqualTo(PageOrientation.Landscape);
    await Assert.That(request.Options.PaperSize).IsEqualTo(PaperSize.A3);
    await Assert.That(request.Options.Margins).IsEqualTo(Margins.None);
    await Assert.That(request.Options.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
    await Assert.That(wire.Position).IsEqualTo(wire.Length);
  }

  [Test]
  public async Task Ten_MiB_HTML_with_markup_and_unicode_fits_without_unnecessary_JSON_expansion()
  {
    const string fragment = "<p>é</p>";
    const int tenMiB = 10 * 1024 * 1024;
    var repeats = tenMiB / Encoding.UTF8.GetByteCount(fragment);
    var html = string.Create(
      repeats * fragment.Length,
      fragment,
      static (span, value) =>
      {
        for (var offset = 0; offset < span.Length; offset += value.Length)
        {
          value.AsSpan().CopyTo(span[offset..]);
        }
      }
    );
    var htmlBytes = Encoding.UTF8.GetByteCount(html);
    using MemoryStream wire = new();
    await WorkerProtocol.WriteRequestAsync(wire, new WorkerRequest(1, JobId, html), TestToken);
    await Assert.That(wire.Length).IsLessThanOrEqualTo(htmlBytes + 512L);
    wire.Position = 0;
    var received = await WorkerProtocol.ReadRequestAsync(wire, TestToken);
    await Assert.That(received.Html).IsEqualTo(html);
  }

  [Test]
  [Arguments(-1)]
  [Arguments(0)]
  [Arguments(WorkerProtocol.MaxRequestBytes + 1)]
  public async Task Invalid_lengths_are_rejected_before_reading_a_payload(int length)
  {
    var bytes = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
    using MemoryStream wire = new(bytes);
    await Assert
      .That(async () => await WorkerProtocol.ReadRequestAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  [Arguments(3)]
  [Arguments(6)]
  public async Task Partial_prefixes_and_payloads_fail_instead_of_becoming_clean_EOF(int bytes)
  {
    var frame = new byte[bytes];
    frame[0] = 10;
    using MemoryStream wire = new(frame);
    await Assert
      .That(async () => await WorkerProtocol.TryReadRequestAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task Clean_EOF_is_distinct_from_a_missing_required_request()
  {
    using MemoryStream wire = new();
    await Assert.That(await WorkerProtocol.TryReadRequestAsync(wire, TestToken)).IsNull();
    await Assert
      .That(async () => await WorkerProtocol.ReadRequestAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  [Arguments("\"extra\":true,")]
  [Arguments("\"html\":\"duplicate\",")]
  [Arguments("\"version\":2,")]
  public async Task Unknown_and_duplicate_JSON_properties_are_rejected(string extra)
  {
    using var wire = JsonFrame($$"""{{{extra}}"version":1,"jobId":"{{JobId}}","html":"safe"}""");
    await Assert
      .That(async () => await WorkerProtocol.ReadRequestAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  [Arguments(
    "{\"version\":2,\"jobId\":\"ac4e49fd-70a2-4df9-a3cb-f8713c0f3b6e\",\"html\":\"safe\"}"
  )]
  [Arguments(
    "{\"version\":1,\"jobId\":\"00000000-0000-0000-0000-000000000000\",\"html\":\"safe\"}"
  )]
  [Arguments("{\"version\":1,\"jobId\":\"ac4e49fd-70a2-4df9-a3cb-f8713c0f3b6e\"}")]
  public async Task Unsupported_contracts_and_missing_required_fields_are_rejected(string json)
  {
    using var wire = JsonFrame(json);
    await Assert
      .That(async () => await WorkerProtocol.ReadRequestAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task Serialization_limit_is_enforced_before_any_partial_frame_is_written()
  {
    using MemoryStream wire = new();
    var request = new WorkerRequest(1, JobId, new string('x', WorkerProtocol.MaxRequestBytes + 1));
    await Assert
      .That(async () => await WorkerProtocol.WriteRequestAsync(wire, request, TestToken))
      .Throws<InvalidDataException>();
    await Assert.That(wire.Length).IsEqualTo(0L);
  }

  [Test]
  public async Task Nonfinite_render_options_are_rejected_without_writing_a_frame()
  {
    using MemoryStream wire = new();
    var request = new WorkerRequest(1, JobId, "safe", new PdfOptions { Scale = double.NaN });
    await Assert
      .That(async () => await WorkerProtocol.WriteRequestAsync(wire, request, TestToken))
      .Throws<InvalidDataException>();
    await Assert.That(wire.Length).IsEqualTo(0L);
  }

  [Test]
  public async Task Reusable_PDF_buffer_reads_chunks_and_only_zero_signals_completion()
  {
    using MemoryStream wire = new();
    byte[] first = [1, 2, 3];
    await WorkerProtocol.WritePdfChunkAsync(wire, first, TestToken);
    await WorkerProtocol.WritePdfChunkAsync(
      wire,
      new byte[WorkerProtocol.MaxChunkBytes],
      TestToken
    );
    await WorkerProtocol.CompletePdfAsync(wire, TestToken);
    wire.Position = 0;
    var buffer = new byte[WorkerProtocol.MaxChunkBytes];
    await Assert.That(await WorkerProtocol.ReadPdfChunkAsync(wire, buffer, TestToken)).IsEqualTo(3);
    await Assert.That(buffer.Take(3)).IsEquivalentTo(first);
    await Assert
      .That(await WorkerProtocol.ReadPdfChunkAsync(wire, buffer, TestToken))
      .IsEqualTo(WorkerProtocol.MaxChunkBytes);
    await Assert.That(await WorkerProtocol.ReadPdfChunkAsync(wire, buffer, TestToken)).IsEqualTo(0);
    await Assert
      .That(async () => await WorkerProtocol.ReadPdfChunkAsync(wire, buffer, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  [Arguments(0)]
  [Arguments(WorkerProtocol.MaxChunkBytes + 1)]
  public async Task Non_pdf_chunk_lengths_cannot_be_written(int length)
  {
    using MemoryStream wire = new();
    await Assert
      .That(async () => await WorkerProtocol.WritePdfChunkAsync(wire, new byte[length], TestToken))
      .Throws<InvalidDataException>();
    await Assert.That(wire.Length).IsEqualTo(0L);
  }

  [Test]
  [Arguments("pdf", "\"RenderFailed\"")]
  [Arguments("error", "null")]
  [Arguments("error", "\"Unauthorized\"")]
  [Arguments("error", "99999")]
  [Arguments("unknown", "null")]
  public async Task Response_status_and_error_kind_must_match_the_worker_contract(
    string status,
    string kind
  )
  {
    using var wire = JsonFrame(
      $$"""{"version":1,"jobId":"{{JobId}}","status":"{{status}}","kind":{{kind}}}"""
    );
    await Assert
      .That(async () => await WorkerProtocol.ReadResponseHeaderAsync(wire, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task Canceled_reads_propagate_cancellation()
  {
    using MemoryStream wire = new(new byte[4]);
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();
    await Assert
      .That(async () => await WorkerProtocol.ReadRequestAsync(wire, cancellation.Token))
      .Throws<OperationCanceledException>();
  }

  private static MemoryStream JsonFrame(string json)
  {
    var payload = Encoding.UTF8.GetBytes(json);
    var bytes = new byte[payload.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
    payload.CopyTo(bytes, 4);
    return new MemoryStream(bytes);
  }
}
