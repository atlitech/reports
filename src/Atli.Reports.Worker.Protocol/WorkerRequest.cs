using Atli.Reports.Engine;

namespace Atli.Reports.Worker.Protocol;

/// <summary>A single versioned, correlated render operation; never carries authentication or engine configuration.</summary>
public sealed record WorkerRequest(
  int Version,
  Guid JobId,
  string Html,
  PdfOptions? Options = null
);

/// <summary>A correlated response preceding either PDF chunks or a sanitized error kind.</summary>
public sealed record WorkerResponseHeader(
  int Version,
  Guid JobId,
  string Status,
  ConversionErrorKind? Kind = null
);
