using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Aspire.Hosting.Tests;

/// <summary>
/// The <c>ReportsEngine__*</c> variables the typed configuration methods set, and the values they reject.
/// </summary>
public class ConfigurationTests
{
  [Test]
  public async Task WithMaxConcurrentConversions_sets_the_concurrency_limit()
  {
    var environment = await Configure(server => server.WithMaxConcurrentConversions(4));

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["ReportsEngine__Concurrency__MaxConcurrentConversions"] = "4",
        }
      );
  }

  [Test]
  [Arguments(0, "0")]
  [Arguments(250, "250")]
  public async Task WithMaxQueueLength_sets_the_queue_length(int length, string expected)
  {
    var environment = await Configure(server => server.WithMaxQueueLength(length));

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["ReportsEngine__Concurrency__MaxQueueLength"] = expected,
        }
      );
  }

  [Test]
  [Arguments(45_000, "00:00:45")]
  [Arguments(0, "00:00:00")]
  [Arguments(-1, "-00:00:00.0010000")]
  public async Task WithQueueTimeout_sets_the_wait_for_a_turn(int milliseconds, string expected)
  {
    var environment = await Configure(server =>
      server.WithQueueTimeout(TimeSpan.FromMilliseconds(milliseconds))
    );

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string?> { ["ReportsEngine__Concurrency__QueueTimeout"] = expected }
      );
  }

  [Test]
  [Arguments(90_000, "00:01:30")]
  [Arguments(86_400_000, "1.00:00:00")]
  [Arguments(-1, "-00:00:00.0010000")]
  public async Task WithConversionTimeout_sets_the_limit_of_a_whole_conversion(
    int milliseconds,
    string expected
  )
  {
    var environment = await Configure(server =>
      server.WithConversionTimeout(TimeSpan.FromMilliseconds(milliseconds))
    );

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string?> { ["ReportsEngine__ConversionTimeout"] = expected }
      );
  }

  [Test]
  public async Task WithBrowserRecycling_sets_both_limits()
  {
    var environment = await Configure(server =>
      server.WithBrowserRecycling(
        maxConversionsPerProcess: 500,
        maxProcessLifetime: TimeSpan.FromMinutes(30)
      )
    );

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["ReportsEngine__Browser__MaxConversionsPerProcess"] = "500",
          ["ReportsEngine__Browser__MaxProcessLifetime"] = "00:30:00",
        }
      );
  }

  [Test]
  public async Task WithBrowserRecycling_sets_only_the_limits_it_is_given()
  {
    var conversions = await Configure(server =>
      server.WithBrowserRecycling(maxConversionsPerProcess: 0)
    );
    var lifetime = await Configure(server =>
      server.WithBrowserRecycling(maxProcessLifetime: Timeout.InfiniteTimeSpan)
    );

    await Assert
      .That(conversions)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["ReportsEngine__Browser__MaxConversionsPerProcess"] = "0",
        }
      );
    await Assert
      .That(lifetime)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["ReportsEngine__Browser__MaxProcessLifetime"] = "-00:00:00.0010000",
        }
      );
  }

  [Test]
  public async Task The_variables_bind_to_the_engine_options_the_server_reads()
  {
    var environment = await Configure(server =>
      server
        .WithMaxConcurrentConversions(3)
        .WithMaxQueueLength(7)
        .WithQueueTimeout(TimeSpan.FromSeconds(12))
        .WithConversionTimeout(TimeSpan.FromMinutes(2))
        .WithBrowserRecycling(
          maxConversionsPerProcess: 250,
          maxProcessLifetime: Timeout.InfiniteTimeSpan
        )
    );

    // The server binds the ReportsEngine section from its environment, where __ separates keys.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(
        environment.Select(variable =>
          KeyValuePair.Create(
            variable.Key.Replace("__", ":", StringComparison.Ordinal),
            variable.Value
          )
        )
      )
      .Build();
    await using var services = new ServiceCollection()
      .AddReportsEngine(configuration.GetSection(ReportsEngineOptions.SectionName))
      .BuildServiceProvider();
    var options = services.GetRequiredService<IOptions<ReportsEngineOptions>>().Value;

    await Assert.That(options.Concurrency.MaxConcurrentConversions).IsEqualTo(3);
    await Assert.That(options.Concurrency.MaxQueueLength).IsEqualTo(7);
    await Assert.That(options.Concurrency.QueueTimeout).IsEqualTo(TimeSpan.FromSeconds(12));
    await Assert.That(options.ConversionTimeout).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.Browser.MaxConversionsPerProcess).IsEqualTo(250);
    await Assert.That(options.Browser.MaxProcessLifetime).IsEqualTo(Timeout.InfiniteTimeSpan);
  }

  [Test]
  public async Task A_later_call_replaces_the_value()
  {
    var environment = await Configure(server =>
      server.WithMaxConcurrentConversions(2).WithMaxConcurrentConversions(6)
    );

    await Assert
      .That(environment["ReportsEngine__Concurrency__MaxConcurrentConversions"])
      .IsEqualTo("6");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task WithMaxConcurrentConversions_needs_at_least_one(int value)
  {
    var server = Server();

    await Assert
      .That(() => server.WithMaxConcurrentConversions(value))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithMaxQueueLength_rejects_a_negative_length()
  {
    var server = Server();

    await Assert.That(() => server.WithMaxQueueLength(-1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithQueueTimeout_rejects_a_negative_wait_other_than_infinite()
  {
    var server = Server();

    await Assert
      .That(() => server.WithQueueTimeout(TimeSpan.FromSeconds(-1)))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1000)]
  public async Task WithConversionTimeout_needs_a_positive_or_infinite_limit(int milliseconds)
  {
    var server = Server();

    await Assert
      .That(() => server.WithConversionTimeout(TimeSpan.FromMilliseconds(milliseconds)))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithBrowserRecycling_needs_a_limit()
  {
    var server = Server();

    await Assert.That(() => server.WithBrowserRecycling()).Throws<ArgumentException>();
  }

  [Test]
  public async Task WithBrowserRecycling_rejects_a_negative_conversion_count()
  {
    var server = Server();

    await Assert
      .That(() => server.WithBrowserRecycling(maxConversionsPerProcess: -1))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1000)]
  public async Task WithBrowserRecycling_needs_a_positive_or_infinite_lifetime(int milliseconds)
  {
    var server = Server();

    await Assert
      .That(() =>
        server.WithBrowserRecycling(maxProcessLifetime: TimeSpan.FromMilliseconds(milliseconds))
      )
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task A_rejected_call_sets_nothing()
  {
    var environment = await Configure(server =>
    {
      try
      {
        server.WithBrowserRecycling(
          maxConversionsPerProcess: 100,
          maxProcessLifetime: TimeSpan.Zero
        );
      }
      catch (ArgumentOutOfRangeException) { }
    });

    await Assert.That(environment).IsEmpty();
  }

  [Test]
  public async Task A_missing_builder_is_rejected()
  {
    IResourceBuilder<ReportsServerResource> server = null!;

    await Assert.That(() => server.WithMaxConcurrentConversions(1)).Throws<ArgumentNullException>();
  }

  private static IResourceBuilder<ReportsServerResource> Server() =>
    AppModel.CreateBuilder().AddReportsServer("reports-server");

  private static Task<Dictionary<string, string?>> Configure(
    Action<IResourceBuilder<ReportsServerResource>> configure
  ) => AppModel.EnvironmentAddedByAsync(Server(), configure);
}
