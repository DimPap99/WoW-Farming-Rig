using System;
using System.Threading;
using System.Threading.Tasks;

namespace PathingAPI.Pathing;

/// <summary>
/// The one lock over <see cref="PPather.PPatherService"/>. Its search is two calls
/// (<c>SetLocations</c> then <c>DoSearch</c>) over shared state, so two callers
/// interleaving return each other's routes - and before this gate the HTTP API, the
/// AnTCP listener and the web UI each guarded it differently (a racy static flag,
/// a private semaphore, nothing), so they could still interleave with one another.
/// <para>
/// Every entry point takes the same gate and only differs in what it does when the
/// gate is held: HTTP answers 429 (<see cref="TryEnter"/>, the V1 client contract),
/// AnTCP and the UI wait (<see cref="Enter"/>, <see cref="EnterAsync"/>).
/// </para>
/// </summary>
public sealed class PatherGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Releaser releaser;

    public PatherGate()
    {
        releaser = new Releaser(this);
    }

    /// <summary>Raised when the gate is taken or released, for status displays.</summary>
    public event Action? BusyChanged;

    public bool IsBusy => gate.CurrentCount == 0;

    /// <summary>Takes the gate if it is free; never waits.</summary>
    public bool TryEnter(out IDisposable lease)
    {
        if (!gate.Wait(0))
        {
            lease = null!;
            return false;
        }

        lease = Entered();
        return true;
    }

    public IDisposable Enter()
    {
        gate.Wait();
        return Entered();
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        return Entered();
    }

    private IDisposable Entered()
    {
        BusyChanged?.Invoke();
        return releaser;
    }

    private void Release()
    {
        gate.Release();
        BusyChanged?.Invoke();
    }

    public void Dispose() => gate.Dispose();

    // One instance is enough: only the holder of the gate ever has it.
    private sealed class Releaser(PatherGate owner) : IDisposable
    {
        public void Dispose() => owner.Release();
    }
}
