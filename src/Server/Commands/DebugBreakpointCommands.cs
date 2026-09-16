using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Metadata;
using dnlib.DotNet;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Code breakpoints on .NET method+IL-offset locations. The module identity comes from the
  /// on-disk dnlib module of a loaded document (<see cref="ModuleId.CreateFromFile"/>),
  /// so breakpoints can be set independent of debug state and bind when the module loads.
  /// </summary>
  internal static class DebugBreakpointCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgAddBreakpoint] = a => AddBreakpoint(ctx, a);
      t[Wire.ReqDbgAddTracepoint] = a => AddTracepoint(ctx, a);
      t[Wire.ReqDbgRemoveBreakpoint] = a => Modify(ctx, a, "bpId", bp => { bp.Remove(); return true; });
      t[Wire.ReqDbgEnableBreakpoint] = a => EnableBreakpoint(ctx, a);
      t[Wire.ReqDbgSetBreakpointCondition] = a => SetCondition(ctx, a);
      t[Wire.ReqDbgListBreakpoints] = a => ListBreakpoints(ctx, a);
    }

    private static object AddBreakpoint(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      uint offset = unchecked((uint)CommandContext.GetInt64(args, 2, "ilOffset"));
      var dbg = ctx.RequireDebugger();
      var location = ResolveLocation(ctx, dbg, filename, token, offset);
      // Add on the debugger dispatcher so it (and Close) run on the thread that owns Dbg objects.
      return dbg.RunOnDbg(() => AddCore(dbg, location, new DbgCodeBreakpointSettings { IsEnabled = true }));
    }

    // A tracepoint is a code breakpoint that logs a (possibly {expr}-interpolated) message and,
    // with continue:true, keeps running instead of stopping — dnSpy's "conditional trace + logfile".
    // Args: documentFilename, token, ilOffset, message, continue?(true), condition?.
    private static object AddTracepoint(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      uint offset = unchecked((uint)CommandContext.GetInt64(args, 2, "ilOffset"));
      string message = CommandContext.GetString(args, 3, "message");
      bool @continue = CommandContext.GetOptBool(args, 4, true);
      string? condition = CommandContext.GetOptString(args, 5);
      var dbg = ctx.RequireDebugger();
      var location = ResolveLocation(ctx, dbg, filename, token, offset);
      var settings = new DbgCodeBreakpointSettings {
        IsEnabled = true,
        Trace = new DbgCodeBreakpointTrace(message, @continue),
      };
      if (!string.IsNullOrEmpty(condition)) {
        settings.Condition = new DbgCodeBreakpointCondition(
            DbgCodeBreakpointConditionKind.IsTrue, condition!);
      }
      return dbg.RunOnDbg(() => AddCore(dbg, location, settings));
    }

    // UI thread: resolve the document, validate the method token (A1 — bad token → XERROR_NOT_FOUND),
    // and build the code location.
    private static DbgDotNetCodeLocation ResolveLocation(
        CommandContext ctx, DebuggerServices dbg, string filename, uint token, uint offset) {
      return ctx.RunOnUI(() => {
        var module = ctx.ResolveModule(filename);
        ctx.ResolveMember<MethodDef>(filename, token, "method");
        var moduleId = ModuleId.CreateFromFile(module);
        // Exact mapping binds precisely at the given IL offset; the 3-arg overload's default mapping
        // left non-entry offsets bound-but-never-hit.
        return dbg.LocationFactory.Create(moduleId, token, offset, DbgILOffsetMapping.Exact);
      });
    }

    // Debugger dispatcher: add the breakpoint. Add (and, on failure, DbgObject.Close) run on the
    // thread that owns Dbg objects so the collection is consistent for subsequent add/remove.
    private static object AddCore(DebuggerServices dbg, DbgDotNetCodeLocation location, DbgCodeBreakpointSettings settings) {
      var bp = dbg.CodeBreakpoints.Add(new DbgCodeBreakpointInfo(location, settings));
      if (bp is null) {
        location.Close();
        throw new CommandException(Wire.ErrDbg, "Breakpoint already exists at that location.");
      }
      return new object[] { bp.Id };
    }

    private static object EnableBreakpoint(CommandContext ctx, object[] args) {
      bool enabled = CommandContext.GetOptBool(args, 1, true);
      return Modify(ctx, args, "bpId", bp => { bp.IsEnabled = enabled; return true; });
    }

    private static object SetCondition(CommandContext ctx, object[] args) {
      string condition = CommandContext.GetString(args, 1, "condition");
      return Modify(ctx, args, "bpId", bp => {
        bp.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, condition);
        return true;
      });
    }

    private static object Modify(CommandContext ctx, object[] args, string idName,
        Func<DbgCodeBreakpoint, bool> action) {
      int id = (int)CommandContext.GetInt64(args, 0, idName);
      var dbg = ctx.RequireDebugger();
      // On the debugger dispatcher so bp.Remove() takes effect synchronously (a second remove then
      // finds nothing → false, and a same-location re-add sees the slot freed).
      return dbg.RunOnDbg(() => {
        var bp = dbg.CodeBreakpoints.Breakpoints.FirstOrDefault(b => b.Id == id);
        if (bp is null) {
          return (object)false;
        }
        return (object)action(bp);
      });
    }

    private static object ListBreakpoints(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long pageOffset = CommandContext.GetOptInt64(args, 0, 0);
      long pageLimit = CommandContext.GetOptInt64(args, 1, 0);
      return dbg.RunOnDbg(() => {
        var rows = new List<object>();
        foreach (var bp in dbg.CodeBreakpoints.Breakpoints) {
          object? moduleName = null, token = null, offset = null;
          if (bp.Location is DbgDotNetCodeLocation loc) {
            moduleName = loc.Module.ModuleName;
            token = (long)loc.Token;
            offset = (long)loc.Offset;
          }
          object? hitCount = dbg.HitCount.GetHitCount(bp) is int hc ? (long)hc : null;
          // Bind state, straight from dnSpy. Without it a hit_count of 0 conflates three very
          // different situations — never reached, bound but the native patch never fired, and never
          // bound at all — which is a real time sink on obfuscated code, where control-flow
          // flattening routinely defeats the IL-offset-to-native mapping.
          // boundCount is deliberately the only "did it bind" field: 0 means the location never
          // resolved to anything the runtime loaded (boundMessage says why), non-zero with no
          // hitCount means it bound and the code has not run yet. An extra isBound bool would just
          // be boundCount > 0, and every wire field is protocol surface forever.
          var bound = bp.BoundBreakpoints;
          var boundMessage = bp.BoundBreakpointsMessage;
          var first = bound.Length > 0 ? bound[0] : null;
          // boundModuleName is the one that matters on a protected sample: it typically loads twice
          // (the on-disk stub and the decrypted in-memory copy) and this says which one the
          // breakpoint actually went into. Address is only meaningful when HasAddress is set.
          rows.Add(new object?[] {
            bp.Id, bp.IsEnabled, bp.Location?.Type, moduleName, token, offset, hitCount,
            (long)bound.Length, boundMessage.Severity.ToString(),
            string.IsNullOrEmpty(boundMessage.Message) ? null : boundMessage.Message,
            first?.Module?.Name,
            first is { HasAddress: true } ? (long)first.Address : (object?)null,
          });
        }
        return CommandContext.PageResult(rows, pageOffset, pageLimit);
      });
    }
  }
}
