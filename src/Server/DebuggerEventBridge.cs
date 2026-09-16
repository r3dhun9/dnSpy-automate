using System;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Subscribes to <see cref="DbgManager"/> events (raised on the debugger dispatcher thread) and
  /// republishes them as MessagePack PUB frames via <see cref="AutomationServer.PublishEvent"/>.
  /// Also caches the last thrown exception for DBG_GET_LAST_EXCEPTION.
  /// </summary>
  internal sealed class DebuggerEventBridge {
    private readonly DebuggerServices _dbg;
    private readonly AutomationServer _server;
    private bool _started;

    // The kind of the most recent break message (set in OnMessage, which fires just before
    // IsRunningChanged), surfaced as the EVENT_PAUSED reason. Reset when the debuggee resumes.
    private string? _lastBreakReason;

    public DebuggerEventBridge(DebuggerServices dbg, AutomationServer server) {
      _dbg = dbg;
      _server = server;
    }

    /// <summary>Hooks the debugger events.</summary>
    public void Start() {
      if (_started) {
        return;
      }
      _started = true;
      var m = _dbg.Manager;
      m.Message += OnMessage;
      m.IsDebuggingChanged += OnIsDebuggingChanged;
      m.IsRunningChanged += OnIsRunningChanged;
      // Emit EVENT_BREAKPOINT_HIT from the per-breakpoint Hit event ("hit and the process WILL be
      // paused"), so condition-false hits and continuing tracepoints (which never pause) don't emit.
      _dbg.CodeBreakpoints.BreakpointsChanged += OnBreakpointsChanged;
      foreach (var bp in _dbg.CodeBreakpoints.Breakpoints) {
        bp.Hit += OnBreakpointHit;
      }
    }

    /// <summary>Unhooks the debugger events.</summary>
    public void Stop() {
      if (!_started) {
        return;
      }
      _started = false;
      var m = _dbg.Manager;
      m.Message -= OnMessage;
      m.IsDebuggingChanged -= OnIsDebuggingChanged;
      m.IsRunningChanged -= OnIsRunningChanged;
      _dbg.CodeBreakpoints.BreakpointsChanged -= OnBreakpointsChanged;
      foreach (var bp in _dbg.CodeBreakpoints.Breakpoints) {
        bp.Hit -= OnBreakpointHit;
      }
    }

    private void OnIsDebuggingChanged(object? sender, EventArgs e) =>
        Publish(_dbg.Manager.IsDebugging ? Wire.EvtDebugStart : Wire.EvtDebugStop);

    private void OnIsRunningChanged(object? sender, EventArgs e) {
      var running = _dbg.Manager.IsRunning;
      if (running == true) {
        _lastBreakReason = null;
        Publish(Wire.EvtResumed);
      } else if (running == false) {
        Publish(Wire.EvtPaused, _lastBreakReason);
      }
    }

    private void OnMessage(object? sender, DbgMessageEventArgs e) {
      switch (e.Kind) {
        case DbgMessageKind.ProcessCreated when e is DbgMessageProcessCreatedEventArgs a:
          Publish(Wire.EvtProcessCreated, a.Process.Id, a.Process.Name);
          break;
        case DbgMessageKind.ProcessExited when e is DbgMessageProcessExitedEventArgs a:
          Publish(Wire.EvtProcessExited, a.Process.Id, a.ExitCode);
          break;
        case DbgMessageKind.ModuleLoaded when e is DbgMessageModuleLoadedEventArgs a:
          Publish(Wire.EvtModuleLoaded, a.Module.Name, a.Module.Filename);
          break;
        case DbgMessageKind.ModuleUnloaded when e is DbgMessageModuleUnloadedEventArgs a:
          Publish(Wire.EvtModuleUnloaded, a.Module.Name);
          break;
        case DbgMessageKind.ThreadCreated when e is DbgMessageThreadCreatedEventArgs a:
          Publish(Wire.EvtThreadCreated, (long)a.Thread.Id);
          break;
        case DbgMessageKind.ThreadExited when e is DbgMessageThreadExitedEventArgs a:
          Publish(Wire.EvtThreadExited, (long)a.Thread.Id);
          break;
        case DbgMessageKind.BoundBreakpoint:
          // The hit itself is published from OnBreakpointHit (real breaks only). Record the reason
          // here (this fires before IsRunningChanged) so a resulting EVENT_PAUSED says "breakpoint".
          _lastBreakReason = "breakpoint";
          break;
        case DbgMessageKind.StepComplete when e is DbgMessageStepCompleteEventArgs a:
          _lastBreakReason = "step";
          Publish(Wire.EvtStepComplete, ThreadId(a.Thread), a.Error);
          break;
        case DbgMessageKind.ExceptionThrown when e is DbgMessageExceptionThrownEventArgs a:
          _lastBreakReason = "exception";
          var ex = a.Exception;
          var snapshot = new object?[] {
            ex.Id.Name, ex.Message, ex.IsFirstChance, ThreadId(ex.Thread),
          };
          _dbg.LastException = snapshot;
          Publish(Wire.EvtException, snapshot[0], snapshot[1], snapshot[2], snapshot[3]);
          break;
        case DbgMessageKind.ProgramBreak:
          _lastBreakReason = "program-break";
          break;
        case DbgMessageKind.EntryPointBreak:
          _lastBreakReason = "entry-point";
          break;
        case DbgMessageKind.Break:
          _lastBreakReason = "user-break";
          break;
        case DbgMessageKind.SetIPComplete:
          _lastBreakReason = "set-ip";
          break;
      }
    }

    private void OnBreakpointsChanged(object? sender, DbgCollectionChangedEventArgs<DbgCodeBreakpoint> e) {
      foreach (var bp in e.Objects) {
        if (e.Added) {
          bp.Hit += OnBreakpointHit;
        } else {
          bp.Hit -= OnBreakpointHit;
        }
      }
    }

    /// <summary>
    /// Fires only when a breakpoint actually pauses the debuggee (excludes condition-false hits and
    /// continuing tracepoints). Publishes EVENT_BREAKPOINT_HIT enriched with the .NET location.
    /// </summary>
    private void OnBreakpointHit(object? sender, DbgBreakpointHitEventArgs e) {
      var bp = e.BoundBreakpoint.Breakpoint;
      if (bp.Location is DbgDotNetCodeLocation loc) {
        Publish(Wire.EvtBreakpointHit, bp.Id, ThreadId(e.Thread),
            loc.Module.ModuleName, (long)loc.Token, (long)loc.Offset);
      } else {
        Publish(Wire.EvtBreakpointHit, bp.Id, ThreadId(e.Thread), null, null, null);
      }
    }

    private static object? ThreadId(DbgThread? thread) => thread is null ? null : (long)thread.Id;

    private void Publish(string evt, params object?[] fields) {
      var frame = new object?[fields.Length + 1];
      frame[0] = evt;
      Array.Copy(fields, 0, frame, 1, fields.Length);
      _server.PublishEvent(Wire.Encode(frame));
    }
  }
}
