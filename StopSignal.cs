using System;
using System.Threading;

namespace NoBorder
{
    /// <summary>
    /// Lets any NoBorder process (CLI or UI) signal a running instance to either stop
    /// watching or bring its window to the front, without needing IPC plumbing.
    /// Backed by named, session-local EventWaitHandles so it works across processes.
    /// </summary>
    internal static class StopSignal
    {
        // Fixed, namespaced names so unrelated apps can't collide with them.
        private const string StopEventName = @"Local\NoBorder_StopSignal_8F2A1C77";
        private const string ShowEventName = @"Local\NoBorder_ShowWindow_8F2A1C77";

        /// <summary>Sends the stop signal to a running watcher. Returns true if one was listening.</summary>
        public static bool Send() => SendPulse(StopEventName);

        /// <summary>Asks a running instance to bring its window to the front. Returns true if one was listening.</summary>
        public static bool SendShowWindow() => SendPulse(ShowEventName);

        private static bool SendPulse(string eventName)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(eventName);
                existing.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false; // nobody was listening
            }
        }

        /// <summary>
        /// Listens for the one-shot stop signal and invokes onStop once when it arrives.
        /// Dispose the returned listener when your watcher stops on its own.
        /// </summary>
        public static IDisposable Listen(Action onStop)
        {
            return new OneShotListener(StopEventName, onStop);
        }

        /// <summary>
        /// Listens for repeated "show window" requests from later launches of the app and
        /// invokes onShowRequested each time. Keeps listening until disposed.
        /// </summary>
        public static IDisposable ListenForShowRequests(Action onShowRequested)
        {
            return new RepeatingListener(ShowEventName, onShowRequested);
        }

        private sealed class OneShotListener : IDisposable
        {
            private readonly EventWaitHandle _handle;
            private readonly Thread _thread;
            private volatile bool _disposed;

            public OneShotListener(string name, Action onSignaled)
            {
                _handle = new EventWaitHandle(false, EventResetMode.ManualReset, name);
                _thread = new Thread(() =>
                {
                    _handle.WaitOne(); // blocks until Send() is called or Dispose unblocks it
                    if (!_disposed)
                    {
                        onSignaled();
                    }
                })
                {
                    IsBackground = true,
                    Name = "NoBorderStopListener"
                };
                _thread.Start();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _handle.Set(); // unblock the wait thread so it can exit
                _thread.Join(1000);
                _handle.Dispose();
            }
        }

        private sealed class RepeatingListener : IDisposable
        {
            private readonly EventWaitHandle _handle;
            private readonly Thread _thread;
            private volatile bool _disposed;

            public RepeatingListener(string name, Action onSignaled)
            {
                // AutoReset: each Set() wakes the waiter exactly once and the handle resets
                // itself, so a second launch later in the session triggers this again rather
                // than firing once and then doing nothing for subsequent launches.
                _handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                _thread = new Thread(() =>
                {
                    while (!_disposed)
                    {
                        _handle.WaitOne();
                        if (!_disposed)
                        {
                            onSignaled();
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "NoBorderShowListener"
                };
                _thread.Start();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _handle.Set(); // unblock the wait thread so its loop can observe _disposed and exit
                _thread.Join(1000);
                _handle.Dispose();
            }
        }
    }
}
