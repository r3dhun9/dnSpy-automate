using System;
using System.Linq;
using System.Text;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Text;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Bundles the MEF-imported debugger services and provides
  /// <see cref="RunOnDbg{T}(Func{CancellationToken, T}, TimeSpan?)"/>, the helper that marshals
  /// reads/control calls onto <see cref="DbgManager.Dispatcher"/> (the debugger engine thread) and
  /// blocks until they complete. Also holds a snapshot of the last thrown exception (updated by
  /// <see cref="DebuggerEventBridge"/>) for DBG_GET_LAST_EXCEPTION.
  ///
  /// Only call it for work that genuinely needs that thread. Several <see cref="DbgManager"/>
  /// members do not: <c>IsDebugging</c>, <c>IsRunning</c> and <c>Processes</c> are plain lock-guarded
  /// reads, and <c>StopDebuggingAll</c>/<c>TerminateAll</c>/<c>DetachAll</c>/<c>RunAll</c> marshal
  /// themselves. Routing those through the dispatcher only makes them wedge along with everything
  /// else when one bad func-eval blocks the queue.
  /// </summary>
  internal sealed class DebuggerServices {
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Budget for the evaluation commands. One EXPAND_VALUE can serialize one func-eval per child,
    /// and dnSpy's worst case for a single func-eval is ~5s (1s eval + 3s Abort + 1s RudeAbort), so
    /// the ordinary <see cref="DispatchTimeout"/> is far too tight for them.
    /// </summary>
    public static readonly TimeSpan EvalDispatchTimeout = TimeSpan.FromSeconds(30);

    private long _consecutiveDispatchTimeouts;

    public DebuggerServices(
        DbgManager manager,
        DbgCodeBreakpointsService codeBreakpoints,
        DbgCodeBreakpointHitCountService hitCount,
        DbgModuleBreakpointsService moduleBreakpoints,
        DbgCallStackService callStack,
        DbgLanguageService languages,
        DbgObjectIdService objectIds,
        DbgExceptionSettingsService exceptionSettings,
        AttachableProcessesService attachableProcesses,
        DbgDotNetCodeLocationFactory locationFactory) {
      Manager = manager;
      CodeBreakpoints = codeBreakpoints;
      HitCount = hitCount;
      ModuleBreakpoints = moduleBreakpoints;
      CallStack = callStack;
      Languages = languages;
      ObjectIds = objectIds;
      ExceptionSettings = exceptionSettings;
      AttachableProcesses = attachableProcesses;
      LocationFactory = locationFactory;
    }

    public DbgManager Manager { get; }
    public DbgCodeBreakpointsService CodeBreakpoints { get; }
    public DbgCodeBreakpointHitCountService HitCount { get; }
    public DbgModuleBreakpointsService ModuleBreakpoints { get; }
    public DbgCallStackService CallStack { get; }
    public DbgLanguageService Languages { get; }
    public DbgObjectIdService ObjectIds { get; }
    public DbgExceptionSettingsService ExceptionSettings { get; }
    public AttachableProcessesService AttachableProcesses { get; }
    public DbgDotNetCodeLocationFactory LocationFactory { get; }

    /// <summary>Snapshot of the most recent thrown exception: [name, message, firstChance, tid].</summary>
    public volatile object?[]? LastException;

    /// <summary>
    /// Consecutive <see cref="RunOnDbg{T}(Func{CancellationToken, T}, TimeSpan?)"/> timeouts since
    /// the last success. 0 means healthy. A growing value means the debugger dispatcher is no longer
    /// draining its queue, which nothing on this side can repair: dnSpy's <c>DbgDispatcher</c>
    /// exposes only <c>CheckAccess</c> and <c>BeginInvoke</c> (which silently no-ops once the
    /// dispatcher has shut down), so there is no reset hook and only restarting dnSpy recovers.
    /// </summary>
    public long ConsecutiveDispatchTimeouts => Interlocked.Read(ref _consecutiveDispatchTimeouts);

    /// <summary>
    /// Runs <paramref name="func"/> on the debugger dispatcher thread and returns its result.
    /// </summary>
    /// <param name="func">The work to run on the dispatcher.</param>
    /// <param name="timeout">How long to wait; <see cref="DispatchTimeout"/> when null.</param>
    /// <returns>The value <paramref name="func"/> produced.</returns>
    public T RunOnDbg<T>(Func<T> func, TimeSpan? timeout = null) =>
        RunOnDbg(_ => func(), timeout);

    /// <summary>
    /// Runs <paramref name="func"/> on the debugger dispatcher thread and returns its result,
    /// handing it a token that is cancelled if the caller gives up waiting. Pass that token into
    /// every dnSpy evaluation API: dnSpy checks it throughout, and it is the only way to abort a
    /// func-eval that has hung. Blocks the caller (poller thread) until it completes; rethrows any
    /// exception.
    /// </summary>
    /// <param name="func">The work to run on the dispatcher.</param>
    /// <param name="timeout">How long to wait; <see cref="DispatchTimeout"/> when null.</param>
    /// <returns>The value <paramref name="func"/> produced.</returns>
    /// <exception cref="CommandException">If the dispatcher did not answer in time.</exception>
    public T RunOnDbg<T>(Func<CancellationToken, T> func, TimeSpan? timeout = null) {
      if (Manager.Dispatcher.CheckAccess()) {
        return func(CancellationToken.None);
      }

      var call = new DispatchCall();
      T result = default!;
      Exception? error = null;
      Manager.Dispatcher.BeginInvoke(() => {
        // A call the poller has already given up on must not run its body: the client retries after
        // a timeout, and this queue is single-threaded, so stale work would pile up in front of
        // every later command and never drain.
        if (call.Abandoned) {
          return;
        }
        try {
          result = func(call.Token);
        } catch (Exception ex) {
          error = ex;
        } finally {
          call.Done.Set();
        }
      });

      if (!call.Done.Wait(timeout ?? DispatchTimeout)) {
        // Cancel first so an in-flight func-eval actually aborts, then report. The call object is
        // deliberately left to the GC: the work item may still be inside func() holding the token,
        // and disposing it under that race is worse than leaking a handful on a dead session.
        call.Abandon();
        throw new CommandException(
            Wire.ErrDbgBusy,
            TimeoutMessage(Interlocked.Increment(ref _consecutiveDispatchTimeouts)));
      }

      Interlocked.Exchange(ref _consecutiveDispatchTimeouts, 0);
      // Deliberately not disposing the token source. dnSpy holds the token inside the
      // DbgEvaluationContext, and Close() on that is itself marshalled, so the token can
      // outlive this call. Disposing it early is a race for no benefit — a CancellationTokenSource
      // with no timer and no registrations is plain garbage the GC handles.
      call.Done.Dispose();
      if (error is not null) {
        throw error;
      }
      return result;
    }

    /// <summary>
    /// Posts an empty work item and waits for it. The dispatcher is FIFO at equal priority, so this
    /// acts as a barrier: anything already queued has run by the time it returns. Use it after a dnSpy
    /// API that marshals itself fire-and-forget — <c>DbgExceptionSettingsService.Modify</c> and the
    /// <c>CurrentThread.Current</c> setter both <c>BeginInvoke</c> and return before the work lands, so
    /// without a barrier we would report success on a change that has not happened yet.
    ///
    /// Must be called from OFF the dispatcher. On the dispatcher thread it runs inline and proves
    /// nothing, since the work it is meant to wait for is queued behind the current item.
    /// </summary>
    public void DrainDbgQueue() => RunOnDbg(() => true);

    /// <summary>Builds the timeout message, escalating once the dispatcher looks truly stuck.</summary>
    /// <param name="consecutiveTimeouts">Timeouts seen since the last success, including this one.</param>
    /// <returns>The message for the XERROR_DBG_BUSY tuple.</returns>
    private static string TimeoutMessage(long consecutiveTimeouts) {
      if (consecutiveTimeouts < 3) {
        return "Debugger dispatcher timed out; the request was cancelled. Retrying is safe.";
      }
      return $"Debugger dispatcher is not responding ({consecutiveTimeouts} consecutive timeouts). " +
          "dnSpy has to be restarted to recover — its debugger dispatcher has no reset hook. " +
          "DBG_STATUS and DBG_IS_DEBUGGING do not use the dispatcher and still answer.";
    }

    /// <summary>
    /// One in-flight <see cref="RunOnDbg{T}(Func{CancellationToken, T}, TimeSpan?)"/> call, shared
    /// between the poller thread and the dispatcher work item.
    /// </summary>
    private sealed class DispatchCall {
      private readonly CancellationTokenSource _cts = new CancellationTokenSource();
      private volatile bool _abandoned;

      /// <summary>Signalled by the work item once it has finished or thrown.</summary>
      public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);

      /// <summary>Cancelled when the poller stops waiting.</summary>
      public CancellationToken Token => _cts.Token;

      /// <summary>True once the poller has given up, so a queued work item skips its body.</summary>
      public bool Abandoned => _abandoned;

      /// <summary>Gives up on the call and cancels its token.</summary>
      public void Abandon() {
        _abandoned = true;
        try {
          _cts.Cancel();
        } catch (Exception) {
          // A cancellation callback inside dnSpy threw; the timeout is reported either way.
        }
      }

    }

    // ---- Lookup helpers (call on the dispatcher thread, i.e. inside RunOnDbg) --------

    public DbgProcess? FindProcess(int pid) =>
        Manager.Processes.FirstOrDefault(p => p.Id == pid);

    public DbgThread? FindThread(ulong tid) =>
        Manager.Processes.SelectMany(p => p.Threads).FirstOrDefault(t => t.Id == tid);

    public DbgThread? CurrentThread => Manager.CurrentThread.Current;
    public DbgProcess? CurrentProcess => Manager.CurrentProcess.Current;
    public DbgRuntime? CurrentRuntime => Manager.CurrentRuntime.Current;

    /// <summary>Resolves a thread by optional id argument, or the current thread.</summary>
    public DbgThread ResolveThread(long? tid) {
      var thread = tid is null ? CurrentThread : FindThread((ulong)tid.Value);
      return thread ?? throw new CommandException(Wire.ErrDbg, "No such thread (is the debuggee paused?).");
    }

    /// <summary>Returns the stack frame at <paramref name="index"/> for a thread (null if OOB).</summary>
    public DbgStackFrame? GetFrame(DbgThread thread, int index) {
      var frames = thread.GetFrames(index + 1);
      return index >= 0 && index < frames.Length ? frames[index] : null;
    }
  }

  /// <summary>An <see cref="IDbgTextWriter"/> that accumulates written text (ignoring color).</summary>
  internal sealed class CapturingDbgTextWriter : IDbgTextWriter {
    private readonly StringBuilder _sb = new StringBuilder();
    public void Write(DbgTextColor color, string? text) => _sb.Append(text);
    public override string ToString() => _sb.ToString();
  }
}
