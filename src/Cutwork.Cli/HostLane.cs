using System.Collections.Concurrent;

namespace Flamoris.Cutwork.Cli;

/// <summary>One UI-free serialization thread for document, history and save completions.</summary>
internal sealed class HostLane : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly int owner = Environment.CurrentManagedThreadId;
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    public Task Dispatch(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId == owner) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { token.ThrowIfCancellationRequested(); action(); completion.SetResult(); }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception e) { completion.SetException(e); }
        }, null);
        return completion.Task;
    }
    public void Run(Func<Task> action)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var work = action();
            _ = work.ContinueWith(_ => queue.CompleteAdding(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
            work.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
    public void Dispose() => queue.Dispose();
}
