using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Debugger session control: start/attach/restart/detach/terminate/stop + state.</summary>
  internal static class DebugSessionCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgStart] = a => Start(ctx, a);
      t[Wire.ReqDbgAttach] = a => Attach(ctx, a);
      t[Wire.ReqDbgRestart] = _ => Control(ctx, m => {
        // Capture CanRestart BEFORE restarting: Restart() tears down the current session, so
        // re-reading it afterwards would report False even when a restart was initiated.
        bool canRestart = m.CanRestart;
        if (canRestart) {
          m.Restart();
        }
        return canRestart;
      });
      t[Wire.ReqDbgDetachAll] = _ => Control(ctx, m => { m.DetachAll(); return true; });
      t[Wire.ReqDbgTerminateAll] = _ => Control(ctx, m => { m.TerminateAll(); return true; });
      t[Wire.ReqDbgStopAll] = _ => Control(ctx, m => { m.StopDebuggingAll(); return true; });
      t[Wire.ReqDbgIsDebugging] = _ => ctx.RequireDebugger().Manager.IsDebugging;
      t[Wire.ReqDbgIsRunning] = _ => {
        var m = ctx.RequireDebugger().Manager;
        // IsRunning is only meaningful while debugging; report null otherwise (matches DBG_STATUS).
        return m.IsDebugging ? m.IsRunning : (bool?)null;
      };
      t[Wire.ReqDbgStatus] = _ => Status(ctx);
    }

    /// <summary>
    /// Aggregate session state in one round-trip:
    /// [isDebugging, isRunning(nullable), currentPid?, currentTid?, processCount, dispatchTimeouts].
    ///
    /// Every value here is a lock-guarded read on <see cref="DbgManager"/>, so this deliberately does
    /// NOT go through <see cref="DebuggerServices.RunOnDbg{T}(Func{System.Threading.CancellationToken, T}, TimeSpan?)"/>.
    /// That is what makes it usable as a health probe: it still answers when a slow func-eval has the
    /// debugger dispatcher backed up, and <c>dispatchTimeouts</c> then reports how many consecutive
    /// dispatches have timed out (0 = healthy).
    /// </summary>
    private static object Status(CommandContext ctx) {
      var dbg = ctx.RequireDebugger();
      var m = dbg.Manager;
      bool isDebugging = m.IsDebugging;
      return new object?[] {
        isDebugging,
        isDebugging ? m.IsRunning : (bool?)null,
        dbg.CurrentProcess?.Id,
        dbg.CurrentThread is { } th ? (long)th.Id : (object?)null,
        m.Processes.Length,
        dbg.ConsecutiveDispatchTimeouts,
      };
    }

    /// <summary>How long to wait for a session-control call to land before answering anyway.</summary>
    private static readonly TimeSpan ControlBarrierTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a session-control call straight off the poller thread, then waits for it to land.
    ///
    /// Restart/DetachAll/TerminateAll/StopDebuggingAll are all either lock-guarded or
    /// self-marshalling inside dnSpy, so they must not go through RunOnDbg — that is what used to
    /// make stopping a session impossible once the dispatcher was busy. But they also return
    /// *before* the work happens, and answering that early is its own bug: the caller starts
    /// polling for the session to end while dnSpy is still tearing it down, and dnSpy's own Locals
    /// window can still be formatting values out of a process whose memory is being unmapped. That
    /// race ends in an AccessViolation on dnSpy's engine thread, which has no exception guard, and
    /// takes the whole process with it.
    ///
    /// So post a FIFO barrier afterwards. A timeout on the barrier is not an error — it only means
    /// the dispatcher is busy and the request is still queued, which is exactly the case where
    /// answering promptly matters most.
    /// </summary>
    /// <param name="ctx">Command context.</param>
    /// <param name="action">The manager call to make.</param>
    /// <returns>Whatever the call reported.</returns>
    private static object Control(CommandContext ctx, Func<DbgManager, bool> action) {
      var dbg = ctx.RequireDebugger();
      bool result = action(dbg.Manager);
      try {
        dbg.RunOnDbg(() => true, ControlBarrierTimeout);
      } catch (CommandException) {
        // Dispatcher busy; the call is queued and the caller can poll DBG_STATUS.
      }
      return result;
    }

    private static object Start(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string path = CommandContext.GetString(args, 0, "path");
      string? cmdline = CommandContext.GetOptString(args, 1);
      string? cwd = CommandContext.GetOptString(args, 2);
      bool breakAtEntry = CommandContext.GetOptBool(args, 3, false);
      // Null means "auto-detect from the target"; an explicit "core"/"framework" overrides it.
      string? runtime = CommandContext.GetOptString(args, 4);

      // Classify the target so we host it the way dnSpy's own "Debug Program" would:
      //  - a native apphost .exe boots CoreCLR itself and runs directly (UseHost = false);
      //  - a managed .NET Core assembly (a .dll OR a bare managed .exe) is NOT self-hosting and
      //    must be launched via the dotnet host ("dotnet exec ..."); launched directly, CoreCLR
      //    fails to bootstrap and the process dies with 0xE0434352;
      //  - a managed .NET Framework .exe is hosted by the machine-wide CLR shim and runs directly.
      TargetKind kind = ClassifyTarget(path);
      bool isFramework = runtime is not null
          ? string.Equals(runtime, "framework", StringComparison.OrdinalIgnoreCase)
          : kind == TargetKind.ManagedFramework;

      CorDebugStartDebuggingOptions options = isFramework
          ? new DotNetFrameworkStartDebuggingOptions()
          : new DotNetStartDebuggingOptions();
      options.Filename = path;
      options.CommandLine = cmdline ?? string.Empty;
      // WorkingDirectory becomes CreateProcess's lpCurrentDirectory, which must be null or a
      // valid directory — an empty string makes the launch fail. Default to the target's folder.
      options.WorkingDirectory = string.IsNullOrWhiteSpace(cwd)
          ? (Path.GetDirectoryName(path) ?? string.Empty)
          : cwd;
      if (breakAtEntry) {
        options.BreakKind = PredefinedBreakKinds.EntryPoint;
      }
      // For .NET (Core): host every managed target through the "exec" verb; only a native
      // apphost runs directly. Leave Host empty so the engine picks the bitness-correct
      // dotnet.exe. A zero ConnectionTimeout must be avoided.
      if (options is DotNetStartDebuggingOptions coreOptions) {
        coreOptions.UseHost = kind != TargetKind.NativeExe;
        coreOptions.HostArguments = "exec";
        if (coreOptions.ConnectionTimeout <= TimeSpan.Zero) {
          coreOptions.ConnectionTimeout = TimeSpan.FromSeconds(10);
        }
      }

      // Start is the top-level bootstrap (dnSpy calls it off the engine dispatcher); it returns
      // a synchronous validation error or null, then launches asynchronously.
      string? error = dbg.Manager.Start(options);
      if (!string.IsNullOrEmpty(error)) {
        throw new CommandException(Wire.ErrDbg, error!);
      }
      return true;
    }

    private static object Attach(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      int pid = (int)CommandContext.GetInt64(args, 0, "pid");
      var processes = dbg.AttachableProcesses
          .GetAttachableProcessesAsync(Array.Empty<string>(), new[] { pid }, Array.Empty<string>(),
              CancellationToken.None)
          .GetAwaiter().GetResult();
      var proc = processes.FirstOrDefault();
      if (proc is null) {
        throw new CommandException(Wire.ErrDbg, $"No attachable .NET process with pid {pid}.");
      }
      return dbg.RunOnDbg(() => { proc.Attach(); return (object)true; });
    }

    /// <summary>How a launch target must be hosted by the debugger engine.</summary>
    private enum TargetKind {
      /// <summary>A native PE (e.g. a .NET Core apphost) that boots the runtime itself.</summary>
      NativeExe,
      /// <summary>A managed .NET Framework assembly, hosted by the machine-wide CLR shim.</summary>
      ManagedFramework,
      /// <summary>A managed .NET Core / .NET 5+ assembly, which must run via the dotnet host.</summary>
      ManagedCore,
    }

    /// <summary>Classifies a launch target by inspecting its PE/metadata.</summary>
    /// <param name="path">Full path to the target file.</param>
    /// <returns>
    /// The target kind. Falls back to a filename heuristic (.exe → native, else managed core)
    /// when the file can't be read or isn't a managed image.
    /// </returns>
    private static TargetKind ClassifyTarget(string path) {
      try {
        // Load from a byte copy so we never hold a lock on the file we're about to launch.
        using var module = ModuleDefMD.Load(File.ReadAllBytes(path));
        return IsFrameworkModule(module) ? TargetKind.ManagedFramework : TargetKind.ManagedCore;
      } catch (Exception) {
        // Not a managed image (native apphost / native exe), or the file is unreadable.
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? TargetKind.NativeExe
            : TargetKind.ManagedCore;
      }
    }

    /// <summary>
    /// Decides whether a managed module targets .NET Framework (vs .NET Core / .NET 5+),
    /// preferring the TargetFrameworkAttribute and falling back to the corlib reference name.
    /// </summary>
    /// <param name="module">The loaded module.</param>
    /// <returns>True for .NET Framework; false for .NET Core / .NET 5+ (the default).</returns>
    private static bool IsFrameworkModule(ModuleDefMD module) {
      var tfm = module.Assembly?.CustomAttributes
          .Find("System.Runtime.Versioning.TargetFrameworkAttribute");
      if (tfm is not null && tfm.ConstructorArguments.Count > 0 &&
          tfm.ConstructorArguments[0].Value?.ToString() is string moniker) {
        if (moniker.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
        if (moniker.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) ||
            moniker.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase)) {
          return false;
        }
      }
      // No usable moniker: mscorlib ⇒ Framework; System.Runtime / System.Private.CoreLib ⇒ Core.
      string? corlib = module.CorLibTypes?.AssemblyRef?.Name;
      return string.Equals(corlib, "mscorlib", StringComparison.OrdinalIgnoreCase);
    }
  }
}
