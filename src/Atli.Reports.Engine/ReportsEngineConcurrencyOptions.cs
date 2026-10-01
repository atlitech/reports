namespace Atli.Reports.Engine;

/// <summary>
/// Configures how many conversions the reports engine runs at once, and how it queues the rest.
/// </summary>
/// <remarks>
/// <para>
/// At most <see cref="MaxConcurrentConversions"/> conversions render at the same time. Further
/// conversions wait in a first-in, first-out queue of at most <see cref="MaxQueueLength"/> entries,
/// each for at most <see cref="QueueTimeout"/>. A conversion that finds the queue full, or that waits
/// longer than <see cref="QueueTimeout"/>, fails with <see cref="ConversionErrorKind.Busy"/>; one
/// canceled while it waits fails with <see cref="ConversionErrorKind.Canceled"/>.
/// </para>
/// <para>
/// Bind these from the <c>ReportsEngine:Concurrency</c> configuration section, for example
/// <c>ReportsEngine:Concurrency:MaxConcurrentConversions</c>.
/// </para>
/// </remarks>
public sealed class ReportsEngineConcurrencyOptions
{
  /// <summary>
  /// The most conversions that render at the same time. Defaults to the number of processors, but at
  /// least 2 and at most 8.
  /// </summary>
  /// <remarks>
  /// Every conversion renders in the same browser process, whose main thread does part of the work of
  /// every conversion, so throughput stops growing beyond a handful of concurrent conversions even on
  /// large machines. Must be at least 1.
  /// </remarks>
  public int MaxConcurrentConversions { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

  /// <summary>
  /// The most conversions that may wait for a turn. Defaults to 100. <c>0</c> rejects a conversion
  /// with <see cref="ConversionErrorKind.Busy"/> as soon as every slot is taken.
  /// </summary>
  public int MaxQueueLength { get; set; } = 100;

  /// <summary>
  /// The longest a conversion waits for a turn before it fails with
  /// <see cref="ConversionErrorKind.Busy"/>. Defaults to 30 seconds.
  /// <see cref="Timeout.InfiniteTimeSpan"/> waits until the conversion is canceled.
  /// </summary>
  public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
