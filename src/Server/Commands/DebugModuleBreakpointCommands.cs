using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Module breakpoints (<see cref="DbgModuleBreakpointsService"/>): break when a module whose
  /// name matches a (wildcard-capable) pattern loads. Unlike code breakpoints these carry no
  /// IL location — just a settings bag — so the handlers are a thin wrapper over the service.
  /// </summary>
  internal static class DebugModuleBreakpointCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgAddModuleBreakpoint] = a => AddBreakpoint(ctx, a);
      t[Wire.ReqDbgRemoveModuleBreakpoint] = a => Modify(ctx, a, bp => { bp.Remove(); return true; });
      t[Wire.ReqDbgEnableModuleBreakpoint] = a => EnableBreakpoint(ctx, a);
      t[Wire.ReqDbgListModuleBreakpoints] = a => ListBreakpoints(ctx, a);
    }

    private static object AddBreakpoint(CommandContext ctx, object[] args) {
      string moduleName = CommandContext.GetString(args, 0, "moduleName");
      bool enabled = CommandContext.GetOptBool(args, 1, true);
      var dbg = ctx.RequireDebugger();
      return ctx.RunOnUI<object>(() => {
        var bp = dbg.ModuleBreakpoints.Add(
            new DbgModuleBreakpointSettings { IsEnabled = enabled, ModuleName = moduleName });
        return new object[] { bp.Id };
      });
    }

    private static object EnableBreakpoint(CommandContext ctx, object[] args) {
      bool enabled = CommandContext.GetOptBool(args, 1, true);
      return Modify(ctx, args, bp => { bp.IsEnabled = enabled; return true; });
    }

    private static object Modify(CommandContext ctx, object[] args,
        Func<DbgModuleBreakpoint, bool> action) {
      int id = (int)CommandContext.GetInt64(args, 0, "bpId");
      var dbg = ctx.RequireDebugger();
      return ctx.RunOnUI<object>(() => {
        var bp = dbg.ModuleBreakpoints.Breakpoints.FirstOrDefault(b => b.Id == id);
        if (bp is null) {
          return false;
        }
        return action(bp);
      });
    }

    private static object ListBreakpoints(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return ctx.RunOnUI<object>(() => {
        var rows = new List<object>();
        foreach (var bp in dbg.ModuleBreakpoints.Breakpoints) {
          rows.Add(new object?[] {
            bp.Id, bp.IsEnabled, bp.ModuleName, bp.IsDynamic, bp.IsInMemory, bp.IsLoaded,
            bp.Order is int order ? (long)order : null, bp.AppDomainName, bp.ProcessName,
          });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }
  }
}
