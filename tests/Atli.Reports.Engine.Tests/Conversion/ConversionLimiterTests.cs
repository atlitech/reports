using Atli.Reports.Engine.Conversion;

namespace Atli.Reports.Engine.Tests.Conversion;

public class ConversionLimiterTests
{
  [Test]
  public async Task Slots_are_granted_up_to_the_limit()
  {
    var limiter = Create(maxConcurrent: 2, maxQueue: 10);

    using var first = await limiter.AcquireAsync(CancellationToken.None);
    using var second = await limiter.AcquireAsync(CancellationToken.None);
    var third = limiter.AcquireAsync(CancellationToken.None).AsTask();

    await Assert.That(limiter.Active).IsEqualTo(2);
    await Assert.That(third.IsCompleted).IsFalse();
    await Assert.That(limiter.Queued).IsEqualTo(1);

    first.Dispose();
    using var granted = await third.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(limiter.Active).IsEqualTo(2);
    await Assert.That(limiter.Queued).IsEqualTo(0);
  }

  [Test]
  public async Task Waiters_get_their_turn_in_arrival_order()
  {
    var limiter = Create(maxConcurrent: 1, maxQueue: 10);
    var held = await limiter.AcquireAsync(CancellationToken.None);
    List<int> order = [];
    Lock orderLock = new();

    var waiters = Enumerable
      .Range(0, 5)
      .Select(async index =>
      {
        using var permit = await limiter.AcquireAsync(CancellationToken.None);
        lock (orderLock)
        {
          order.Add(index);
        }
      })
      .ToList();

    held.Dispose();
    await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(string.Join(",", order)).IsEqualTo("0,1,2,3,4");
  }

  [Test]
  public async Task A_full_queue_rejects_at_once()
  {
    var limiter = Create(maxConcurrent: 1, maxQueue: 1);
    using var held = await limiter.AcquireAsync(CancellationToken.None);
    var queued = limiter.AcquireAsync(CancellationToken.None).AsTask();

    await Assert
      .That(async () => await limiter.AcquireAsync(CancellationToken.None))
      .Throws<ConversionBusyException>();
    await Assert.That(queued.IsCompleted).IsFalse();
  }

  [Test]
  public async Task No_queue_rejects_as_soon_as_every_slot_is_taken()
  {
    var limiter = Create(maxConcurrent: 1, maxQueue: 0);
    using var held = await limiter.AcquireAsync(CancellationToken.None);

    await Assert
      .That(async () => await limiter.AcquireAsync(CancellationToken.None))
      .Throws<ConversionBusyException>();
  }

  [Test]
  public async Task Waiting_longer_than_the_queue_timeout_is_busy_and_leaves_the_queue()
  {
    var limiter = Create(
      maxConcurrent: 1,
      maxQueue: 5,
      queueTimeout: TimeSpan.FromMilliseconds(50)
    );
    using var held = await limiter.AcquireAsync(CancellationToken.None);

    await Assert
      .That(async () => await limiter.AcquireAsync(CancellationToken.None))
      .Throws<ConversionBusyException>();
    await Assert.That(limiter.Queued).IsEqualTo(0);
  }

  [Test]
  public async Task Canceling_a_waiter_removes_it_and_keeps_the_slot_count()
  {
    var limiter = Create(maxConcurrent: 1, maxQueue: 5);
    var held = await limiter.AcquireAsync(CancellationToken.None);
    using CancellationTokenSource cancellation = new();
    var canceled = limiter.AcquireAsync(cancellation.Token).AsTask();
    var next = limiter.AcquireAsync(CancellationToken.None).AsTask();

    await cancellation.CancelAsync();
    await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
    held.Dispose();
    using var granted = await next.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(limiter.Active).IsEqualTo(1);
    await Assert.That(limiter.Queued).IsEqualTo(0);
  }

  [Test]
  public async Task Releasing_a_permit_twice_frees_one_slot()
  {
    var limiter = Create(maxConcurrent: 2, maxQueue: 0);
    var permit = await limiter.AcquireAsync(CancellationToken.None);

    permit.Dispose();
    permit.Dispose();

    await Assert.That(limiter.Active).IsEqualTo(0);
  }

  private static ConversionLimiter Create(
    int maxConcurrent,
    int maxQueue,
    TimeSpan? queueTimeout = null
  ) =>
    new(
      new ReportsEngineConcurrencyOptions
      {
        MaxConcurrentConversions = maxConcurrent,
        MaxQueueLength = maxQueue,
        QueueTimeout = queueTimeout ?? TimeSpan.FromSeconds(30),
      }
    );
}
