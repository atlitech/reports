namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>A readiness probe whose answers a test sets; every renderer is ready by default.</summary>
internal sealed class FakeReadinessProbe(Journal journal) : IReadinessProbe
{
  public Func<Uri, CancellationToken, Task<ReadinessAnswer>> Answer { get; set; } =
    (_, _) => Task.FromResult(ReadinessAnswer.Ready);

  /// <summary>Answers <paramref name="answers"/> in turn, then the last one for good.</summary>
  public void AnswerInTurn(params ReadinessAnswer[] answers)
  {
    var next = -1;
    Answer = (_, _) =>
      Task.FromResult(answers[Math.Min(Interlocked.Increment(ref next), answers.Length - 1)]);
  }

  public Task<ReadinessAnswer> ProbeAsync(Uri renderer, CancellationToken cancellationToken)
  {
    journal.Add($"probe {renderer}");
    return Answer(renderer, cancellationToken);
  }
}
