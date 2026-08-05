using System;
using System.Threading;

namespace LatencyBench.Tests;

/// <summary>
/// Regression tests for the single-instance guard's mutex ownership handling.
///
/// The original OnExit released the mutex unconditionally. That is correct for the first instance,
/// which owns it, but a second instance only ever holds a *handle* to the mutex the first one owns
/// — and ReleaseMutex() on a mutex you do not own throws ApplicationException rather than doing
/// nothing. The result was that launching a second copy showed the "already running" notice and
/// then died with an unhandled exception on the way out, logged in the Windows Application event
/// log as "Object synchronization method was called from an unsynchronized block of code" with
/// App.OnExit at the top of the stack.
///
/// Note that Win32 mutex ownership is per-*thread*, not per-handle: two Mutex objects opened on the
/// same thread would let the second one re-acquire recursively and hide the bug entirely. Each test
/// below therefore parks the owner on its own thread, which is what actually models a separate
/// process holding the mutex.
/// </summary>
public sealed class SingleInstanceMutexTests
{
    private static string UniqueName() => "LatencyBenchTests_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Holds a named mutex on a dedicated thread until disposed, standing in for the already-running
    /// first instance.
    /// </summary>
    private sealed class MutexHolder : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;

        public MutexHolder(string name)
        {
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
                if (!createdNew)
                {
                    mutex.WaitOne();
                }

                _acquired.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            })
            { IsBackground = true };

            _thread.Start();
            _acquired.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _acquired.Dispose();
            _release.Dispose();
        }
    }

    [Fact]
    public void ReleasingAMutexThisProcessDoesNotOwnThrows()
    {
        string name = UniqueName();
        using var firstInstance = new MutexHolder(name);

        // The second instance: same name, but it did not create it, so initiallyOwned is ignored
        // and it holds a handle without ownership.
        using var second = new Mutex(initiallyOwned: true, name, out bool secondCreatedNew);
        Assert.False(secondCreatedNew);
        Assert.False(second.WaitOne(TimeSpan.Zero));

        // This is exactly what the old OnExit did, and exactly why the second instance crashed.
        Assert.Throws<ApplicationException>(() => second.ReleaseMutex());
    }

    [Fact]
    public void TrackingOwnershipPreventsTheSecondInstanceFromThrowingOnExit()
    {
        string name = UniqueName();
        using var firstInstance = new MutexHolder(name);

        using var second = new Mutex(initiallyOwned: true, name, out bool secondCreatedNew);
        bool secondOwns = secondCreatedNew || second.WaitOne(TimeSpan.Zero);
        Assert.False(secondOwns);

        // The fixed shutdown path: release only when ownership was actually established. The second
        // instance must be able to run its whole exit sequence without throwing.
        var secondExit = Record.Exception(() =>
        {
            if (secondOwns)
            {
                second.ReleaseMutex();
            }
        });
        Assert.Null(secondExit);
    }

    [Fact]
    public void TheFirstInstanceStillOwnsAndReleasesNormally()
    {
        string name = UniqueName();

        using var first = new Mutex(initiallyOwned: true, name, out bool createdNew);
        bool owns = createdNew;
        Assert.True(owns);

        // The guard must not have made the normal single-instance shutdown a no-op: the first
        // instance still releases, so a relaunch is not left waiting on a mutex nobody freed.
        var exit = Record.Exception(() =>
        {
            if (owns)
            {
                first.ReleaseMutex();
            }
        });
        Assert.Null(exit);
    }

    [Fact]
    public void AnAbandonedMutexIsClaimedRatherThanReportedAsAlreadyRunning()
    {
        string name = UniqueName();
        bool claimed;

        // A previous instance that exits without releasing — Task Manager "End task", or the crash
        // this fix removes — abandons the mutex rather than releasing it.
        var abandoner = new Thread(() =>
        {
            var m = new Mutex(initiallyOwned: true, name, out _);
            // Deliberately no ReleaseMutex: the thread ends still holding it.
        });
        abandoner.Start();
        abandoner.Join();

        using var next = new Mutex(initiallyOwned: true, name, out bool createdNew);
        if (createdNew)
        {
            claimed = true;
        }
        else
        {
            try
            {
                claimed = next.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // Ownership transfers to this waiter as of the throw, so this counts as acquired -
                // a previous crash must not permanently lock the user out of their own app.
                claimed = true;
            }
        }

        Assert.True(claimed);

        var release = Record.Exception(() => next.ReleaseMutex());
        Assert.Null(release);
    }
}
