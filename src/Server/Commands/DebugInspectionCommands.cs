using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Debugger;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Processes/threads/modules/memory enumeration and call-stack inspection.</summary>
  internal static class DebugInspectionCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgListProcesses] = a => ListProcesses(ctx, a);
      t[Wire.ReqDbgListThreads] = a => ListThreads(ctx, a);
      t[Wire.ReqDbgListModules] = a => ListModules(ctx, a);
      t[Wire.ReqDbgReadMemory] = a => ReadMemory(ctx, a);
      t[Wire.ReqDbgCurrentThread] = _ => CurrentThread(ctx);
      t[Wire.ReqDbgFreezeThread] = a => FreezeThread(ctx, a, freeze: true);
      t[Wire.ReqDbgThawThread] = a => FreezeThread(ctx, a, freeze: false);
      t[Wire.ReqDbgSwitchThread] = a => SwitchThread(ctx, a);
      t[Wire.ReqDbgGetCallstack] = a => GetCallstack(ctx, a);
      t[Wire.ReqDbgSelectFrame] = a => SelectFrame(ctx, a);
    }

    /// <summary>
    /// Freezes (true) or thaws (false) a thread and returns the resulting frozen state. dnSpy
    /// applies Freeze/Thaw asynchronously, so the live SuspendedCount lags by one call and is not
    /// sampled; Freeze/Thaw is a capped (0/1, non-stacking) toggle, so the commanded outcome is
    /// authoritative.
    /// </summary>
    private static object FreezeThread(CommandContext ctx, object[] args, bool freeze) {
      var dbg = ctx.RequireDebugger();
      long tid = CommandContext.GetInt64(args, 0, "threadId");
      return dbg.RunOnDbg(() => {
        var thread = dbg.ResolveThread(tid);
        if (freeze) {
          thread.Freeze();
        } else {
          thread.Thaw();
        }
        return (object)freeze;  // resulting state: true = frozen, false = thawed
      });
    }

    /// <summary>Makes the given thread the debugger's current thread (for eval/callstack context).</summary>
    private static object SwitchThread(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long tid = CommandContext.GetInt64(args, 0, "threadId");
      dbg.RunOnDbg(() => {
        var thread = dbg.ResolveThread(tid);
        dbg.Manager.CurrentThread.Current = thread;
        return true;
      });
      // The CurrentThread setter BeginInvoke's its own work item, so the switch has not landed yet;
      // without this a following GET_LOCALS could still read the old thread. The barrier has to be
      // posted from off the dispatcher — inside RunOnDbg it would run inline and prove nothing.
      dbg.DrainDbgQueue();
      return true;
    }

    private static object ListProcesses(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return dbg.RunOnDbg(() => CommandContext.PageResult(
          dbg.Manager.Processes.Select(p => (object)new object?[] {
            p.Id, p.Name, p.Bitness, p.State.ToString(), p.IsRunning,
          }), offset, limit));
    }

    private static object ListThreads(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long? pid = args.Length > 0 && args[0] is not null ? CommandContext.GetInt64(args, 0, "pid") : null;
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return dbg.RunOnDbg(() => {
        var process = ResolveProcess(dbg, pid);
        var rows = process is null ? Enumerable.Empty<object>() : process.Threads.Select(t => (object)new object?[] {
          (long)t.Id, ToLong(t.ManagedId), t.Name, t.Kind, t.IsMain,
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListModules(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long? pid = args.Length > 0 && args[0] is not null ? CommandContext.GetInt64(args, 0, "pid") : null;
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return dbg.RunOnDbg(() => {
        var process = ResolveProcess(dbg, pid);
        // IsDynamic means Reflection.Emit specifically, so it is False for a module loaded from a
        // byte[] — IsInMemory is the flag that says "not backed by a file on disk", and it is what
        // tells a caller which module to aim DBG_DUMP_MODULE at. ImageLayout crosses the wire as a
        // string so a future dnSpy value can never turn a good response into a validation error.
        var rows = process is null ? Enumerable.Empty<object>()
            : process.Runtimes.SelectMany(r => r.Modules).Select(m => (object)new object?[] {
              m.Name, m.Filename, (long)m.Address, (long)m.Size, m.IsDynamic, m.Order,
              m.IsInMemory, m.ImageLayout.ToString(), m.IsExe, m.IsOptimized, m.Version,
            });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ReadMemory(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long pid = CommandContext.GetInt64(args, 0, "pid");
      ulong address = unchecked((ulong)CommandContext.GetInt64(args, 1, "address"));
      int size = (int)CommandContext.GetInt64(args, 2, "size");
      return dbg.RunOnDbg(() => {
        var process = dbg.FindProcess((int)pid)
            ?? throw new CommandException(Wire.ErrDbg, $"No such process: {pid}");
        return (object)process.ReadMemory(address, size);
      });
    }

    private static object CurrentThread(CommandContext ctx) {
      var dbg = ctx.RequireDebugger();
      return dbg.RunOnDbg(() => {
        var thread = dbg.CurrentThread;
        return thread is null ? (object?)null : new object?[] { thread.Process.Id, (long)thread.Id };
      })!;
    }

    private static object GetCallstack(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long? tid = args.Length > 0 && args[0] is not null ? CommandContext.GetInt64(args, 0, "threadId") : null;
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return dbg.RunOnDbg(() => {
        var thread = dbg.ResolveThread(tid);
        var frames = thread.GetFrames(512);
        var rows = new List<object>();
        for (int i = 0; i < frames.Length; i++) {
          var f = frames[i];
          rows.Add(new object?[] {
            i,
            f.HasFunctionToken ? (long)f.FunctionToken : (object?)null,
            (long)f.FunctionOffset,
            f.Module?.Name,
            f.Location is not null,
          });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object SelectFrame(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      int index = (int)CommandContext.GetInt64(args, 0, "index");
      return dbg.RunOnDbg(() => {
        dbg.CallStack.ActiveFrameIndex = index;
        return (object)true;
      });
    }

    private static DbgProcess? ResolveProcess(DebuggerServices dbg, long? pid) =>
        pid is null ? (dbg.CurrentProcess ?? dbg.Manager.Processes.FirstOrDefault()) : dbg.FindProcess((int)pid.Value);

    private static object? ToLong(ulong? value) => value is null ? null : (long)value.Value;
  }
}
