using System;
using System.Collections.Generic;
using dnSpy.Contracts.Debugger.Steppers;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Break/run/step execution control.</summary>
  internal static class DebugExecutionCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      // BreakAll is lock-guarded and RunAll marshals itself onto the debugger dispatcher, so neither
      // needs RunOnDbg — and keeping them off it means break/run still answer while a slow func-eval
      // has the dispatcher queue backed up.
      t[Wire.ReqDbgBreakAll] = _ => { ctx.RequireDebugger().Manager.BreakAll(); return true; };
      t[Wire.ReqDbgRunAll] = _ => { ctx.RequireDebugger().Manager.RunAll(); return true; };
      t[Wire.ReqDbgRunProcess] = a => RunProcess(ctx, a);
      t[Wire.ReqDbgStep] = a => Step(ctx, a);
    }

    private static object RunProcess(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      int pid = (int)CommandContext.GetInt64(args, 0, "pid");
      return dbg.RunOnDbg(() => {
        var process = dbg.FindProcess(pid)
            ?? throw new CommandException(Wire.ErrDbg, $"No such process: {pid}");
        dbg.Manager.Run(process);
        return (object)true;
      });
    }

    private static object Step(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long tid = CommandContext.GetInt64(args, 0, "threadId");
      string kindStr = CommandContext.GetString(args, 1, "kind");
      DbgStepKind kind = kindStr.ToLowerInvariant() switch {
        "into" => DbgStepKind.StepInto,
        "over" => DbgStepKind.StepOver,
        "out" => DbgStepKind.StepOut,
        _ => throw new CommandException(Wire.ErrBadArgs, $"Unknown step kind: {kindStr}"),
      };
      return dbg.RunOnDbg(() => {
        var thread = dbg.ResolveThread(tid);
        var stepper = thread.CreateStepper();
        stepper.Step(kind, autoClose: true);  // completion arrives via EVENT_STEP_COMPLETE
        return (object)true;
      });
    }
  }
}
