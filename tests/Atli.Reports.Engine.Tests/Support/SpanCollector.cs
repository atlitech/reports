using System.Collections.Concurrent;
using System.Diagnostics;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// Records the engine spans of one test. It starts a root span of its own and keeps only the spans
/// in that trace, so conversions that other tests run at the same time do not show up.
/// </summary>
/// <remarks>
/// Create it with <c>using var spans = new SpanCollector();</c> at the start of a test: the root
/// becomes <see cref="Activity.Current"/> for the rest of the test, so the conversions the test
/// starts are its children.
/// </remarks>
internal sealed class SpanCollector : IDisposable
{
  private const string TestSourceName = "Atli.Reports.Engine.Tests";

  private static readonly ActivitySource TestSource = new(TestSourceName);

  private readonly ConcurrentQueue<Activity> _stopped = new();
  private readonly ActivityListener _listener;
  private readonly Activity _root;

  public SpanCollector()
  {
    _listener = new ActivityListener
    {
      ShouldListenTo = source =>
        source.Name is ReportsEngineTelemetry.ActivitySourceName or TestSourceName,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = OnStopped,
    };
    ActivitySource.AddActivityListener(_listener);

    // A trace of its own, even if the test runner has a span running.
    Activity.Current = null;
    _root = TestSource.StartActivity("test")!;
  }

  /// <summary>
  /// The test's root span; conversions the test runs are its children.
  /// </summary>
  public Activity Root => _root;

  /// <summary>
  /// The engine spans of this test that have ended, in the order they ended.
  /// </summary>
  public IReadOnlyList<Activity> Spans =>
    [.. _stopped.Where(span => span.Source.Name == ReportsEngineTelemetry.ActivitySourceName)];

  /// <summary>
  /// The ended spans named <paramref name="name"/>.
  /// </summary>
  public IReadOnlyList<Activity> Named(string name) =>
    [.. Spans.Where(span => span.OperationName == name)];

  /// <summary>
  /// The one ended span named <paramref name="name"/>.
  /// </summary>
  public Activity Single(string name) => Named(name).Single();

  /// <summary>
  /// The names of <paramref name="parent"/>'s ended children, ordered by start time.
  /// </summary>
  public IReadOnlyList<string> ChildrenOf(Activity parent) =>
    [
      .. Spans
        .Where(span => span.ParentSpanId == parent.SpanId)
        .OrderBy(span => span.StartTimeUtc)
        .Select(span => span.OperationName),
    ];

  public void Dispose()
  {
    _root.Dispose();
    _listener.Dispose();
  }

  private void OnStopped(Activity activity)
  {
    // The root is assigned after the listener starts; spans of other traces are ignored.
    if (_root is { } root && activity.TraceId == root.TraceId)
    {
      _stopped.Enqueue(activity);
    }
  }
}
