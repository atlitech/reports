namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Starts work that several requests share (a record lookup, a sandbox's resume) outside the
/// request that happens to start it.
/// </summary>
/// <remarks>
/// The work runs on the thread pool without the starting request's execution context: no
/// <see cref="System.Diagnostics.Activity.Current"/> (so neither its trace context nor its baggage,
/// which ASP.NET Core takes from the caller's headers, can reach Key Vault or the Sandboxes data
/// plane), and none of that request's logging scopes. The work also cannot be canceled by that
/// request; it bounds itself.
/// </remarks>
internal static class SharedWork
{
  public static Task<T> Run<T>(Func<Task<T>> work)
  {
    using (ExecutionContext.SuppressFlow())
    {
      return Task.Run(work, CancellationToken.None);
    }
  }

  public static Task Run(Func<Task> work)
  {
    using (ExecutionContext.SuppressFlow())
    {
      return Task.Run(work, CancellationToken.None);
    }
  }
}
