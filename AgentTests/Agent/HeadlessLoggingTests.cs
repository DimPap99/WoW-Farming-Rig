using HeadlessServer.Hosting;

using Serilog;
using Serilog.Core;
using Serilog.Events;

using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// The console behind its queue (known issue 64): a click into the Windows 10 console blocks every
/// write to it, and the bot's threads, logging, stopped with it.
/// </summary>
public sealed class HeadlessLoggingTests
{
    [Fact]
    public void A_console_that_blocks_holds_no_thread_that_logs()
    {
        using ManualResetEventSlim release = new();
        BlockingSink console = new(release);
        Logger log = new LoggerConfiguration().WriteTo.Queued(to => to.Sink(console)).CreateLogger();

        Task logging = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                log.Information("line {Number}", i);
            }
        }, TestContext.Current.CancellationToken);
        bool returned = logging.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        release.Set();
        log.Dispose();

        Assert.True(returned, "logging waited on the console");
        Assert.Equal(100, console.Count);
    }

    private sealed class BlockingSink(ManualResetEventSlim release) : ILogEventSink
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Emit(LogEvent logEvent)
        {
            release.Wait();
            Interlocked.Increment(ref count);
        }
    }
}
