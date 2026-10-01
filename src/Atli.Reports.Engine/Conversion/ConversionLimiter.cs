using System.Diagnostics;
using System.Globalization;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Bounds how many conversions run at once and queues the rest in arrival order.
/// </summary>
/// <remarks>
/// A conversion takes a slot if one is free and nobody is queued; otherwise it joins the queue,
/// unless the queue is full. A released slot passes straight to the oldest waiter, so the queue is
/// strictly first-in, first-out. Waiters that time out or are canceled leave the queue.
/// </remarks>
internal sealed class ConversionLimiter
{
  private readonly int _maxConcurrent;
  private readonly int _maxQueueLength;
  private readonly TimeSpan _queueTimeout;
  private readonly EngineMetrics? _metrics;
  private readonly Lock _lock = new();
  private readonly LinkedList<Waiter> _queue = new();
  private int _active;
  private int _peakActive;

  public ConversionLimiter(IOptions<ReportsEngineOptions> options, EngineMetrics metrics)
    : this(options.Value.Concurrency, metrics) { }

  internal ConversionLimiter(ReportsEngineConcurrencyOptions options, EngineMetrics? metrics = null)
  {
    _maxConcurrent = options.MaxConcurrentConversions;
    _maxQueueLength = options.MaxQueueLength;
    _queueTimeout = options.QueueTimeout;
    _metrics = metrics;
  }

  /// <summary>
  /// The conversions holding a slot.
  /// </summary>
  public int Active
  {
    get
    {
      lock (_lock)
      {
        return _active;
      }
    }
  }

  /// <summary>
  /// The conversions waiting for a slot.
  /// </summary>
  public int Queued
  {
    get
    {
      lock (_lock)
      {
        return _queue.Count;
      }
    }
  }

  /// <summary>
  /// The most slots ever held at once. Exposed for tests.
  /// </summary>
  internal int PeakActive
  {
    get
    {
      lock (_lock)
      {
        return _peakActive;
      }
    }
  }

  /// <summary>
  /// Waits for a slot. Dispose the returned permit to release it.
  /// </summary>
  /// <exception cref="ConversionBusyException">The queue is full, or the wait exceeded the queue timeout.</exception>
  /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while waiting.</exception>
  public async ValueTask<Permit> AcquireAsync(CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();

    Waiter waiter;
    lock (_lock)
    {
      if (_active < _maxConcurrent && _queue.Count == 0)
      {
        TakeSlotLocked();
        return new Permit(this);
      }

      if (_queue.Count >= _maxQueueLength)
      {
        throw new ConversionBusyException(
          string.Create(
            CultureInfo.InvariantCulture,
            $"The engine is busy: {_active} conversions are running and {_queue.Count} are waiting, the most allowed."
          )
        );
      }

      waiter = new Waiter(Stopwatch.GetTimestamp());
      waiter.Node = _queue.AddLast(waiter);
      _metrics?.Enqueued();
    }

    try
    {
      await waiter.Task.WaitAsync(_queueTimeout, cancellationToken);
      return new Permit(this);
    }
    catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
    {
      lock (_lock)
      {
        if (waiter.Node is { } node)
        {
          _queue.Remove(node);
          waiter.Node = null;
          _metrics?.Dequeued(waiter.EnqueuedTimestamp);
          if (exception is TimeoutException)
          {
            throw new ConversionBusyException(
              string.Create(
                CultureInfo.InvariantCulture,
                $"The engine is busy: the conversion waited {_queueTimeout.TotalSeconds:0.###}s for a turn."
              ),
              exception
            );
          }

          throw;
        }
      }

      // The slot was handed over just as the wait ended. A canceled caller gives it back; a timed
      // out one keeps it, since it got its turn after all.
      if (exception is OperationCanceledException)
      {
        Release();
        throw;
      }

      return new Permit(this);
    }
  }

  private void TakeSlotLocked()
  {
    _active++;
    _peakActive = Math.Max(_peakActive, _active);
    _metrics?.SlotTaken();
  }

  private void Release()
  {
    lock (_lock)
    {
      // Hand the slot straight to the oldest waiter, so arrivals cannot overtake the queue.
      if (_queue.First is { } first)
      {
        _queue.RemoveFirst();
        first.Value.Node = null;
        _metrics?.Dequeued(first.Value.EnqueuedTimestamp);
        first.Value.TrySetResult();
        return;
      }

      _active--;
      _metrics?.SlotReleased();
    }
  }

  /// <summary>
  /// A held slot.
  /// </summary>
  public sealed class Permit(ConversionLimiter limiter) : IDisposable
  {
    private bool _released;

    public void Dispose()
    {
      if (!Interlocked.Exchange(ref _released, true))
      {
        limiter.Release();
      }
    }
  }

  private sealed class Waiter(long enqueuedTimestamp)
    : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  {
    public long EnqueuedTimestamp { get; } = enqueuedTimestamp;

    public LinkedListNode<Waiter>? Node { get; set; }
  }
}

/// <summary>
/// The engine has no capacity for the conversion: the queue is full, or the conversion waited too
/// long for a turn.
/// </summary>
internal sealed class ConversionBusyException : Exception
{
  public ConversionBusyException() { }

  public ConversionBusyException(string message)
    : base(message) { }

  public ConversionBusyException(string message, Exception innerException)
    : base(message, innerException) { }
}
