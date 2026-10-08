using System;
using System.Threading;

namespace Game;

/// <summary>
/// A tick run on a background thread of its own, then again every <c>interval</c>, until it is
/// disposed or the token it was given is cancelled. It waits on a token source of its own, linked to
/// the given one, so the given source may be disposed first: waiting on a disposed source's handle
/// throws, and on a thread nothing catches that ends the process (known issue 63). A tick that throws
/// is handed to <c>failed</c>, and the next one runs on time. <see cref="Dispose"/> stops the thread
/// and waits for it, so whoever disposes the owner ends the thread with it.
/// </summary>
public sealed class PollThread : IDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Thread thread;

    public PollThread(string name, TimeSpan interval, Action tick, Action<Exception> failed, CancellationToken token)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken own = stop.Token;
        thread = new Thread(() =>
        {
            while (!own.IsCancellationRequested)
            {
                try
                {
                    tick();
                }
                catch (Exception ex)
                {
                    failed(ex);
                }

                own.WaitHandle.WaitOne(interval);
            }
        })
        { IsBackground = true, Name = name };
        thread.Start();
    }

    /// <summary>Whether the thread still runs.</summary>
    public bool IsAlive => thread.IsAlive;

    public void Dispose()
    {
        stop.Cancel();
        if (Thread.CurrentThread == thread)
        {
            // Disposed by its own tick: the loop ends after it, and still needs the source.
            return;
        }

        thread.Join();
        stop.Dispose();
    }
}
