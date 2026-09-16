using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Dumps a module out of a live debuggee. This is the recovery step for a packed or encrypted
  /// assembly: the file on disk only holds a stub, and the real metadata exists nowhere until the
  /// sample has decrypted itself and handed the bytes to the runtime — so the only way to reach it is
  /// to pause after that has happened and read it back out of the process.
  /// </summary>
  internal static class DebugDumpCommands {
    /// <summary>Ceiling on how much we will try to read out of a debuggee, to bound a bogus Size.</summary>
    private const int MaxDumpBytes = 256 * 1024 * 1024;

    /// <summary>How long to wait for dnSpy to rebuild a dynamic module's metadata.</summary>
    private static readonly TimeSpan CordebugTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Rebuilt by dnSpy from the runtime's own metadata (dynamic modules only).</summary>
    private const string StrategyMetadata = "metadata";

    /// <summary>Read straight out of the module's in-memory image.</summary>
    private const string StrategyMemory = "memory";

    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgDumpModule] = a => DumpModule(ctx, a);
    }

    // Args: pid?, address, outputPath.
    // The module is keyed by load address rather than by name: the address is unique, DBG_LIST_MODULES
    // already reports it, and it needs no disambiguation for the one case this command exists for --
    // a packer's module whose "name" is not a real path and may not even be unique.
    // Returns [outputPath, strategy, imageLayout, byteCount, isComplete, methodsTotal,
    //          methodsRvaOutsideImage, warning].
    private static object DumpModule(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long? pid = args.Length > 0 && args[0] is not null
          ? CommandContext.GetInt64(args, 0, "pid")
          : null;
      ulong address = unchecked((ulong)CommandContext.GetInt64(args, 1, "address"));
      string outputPath = CommandContext.GetString(args, 2, "outputPath");

      // Resolve and read the plain image on the dispatcher, then hand off. Everything expensive --
      // dnSpy's metadata rebuild, the dnlib round-trip, the file write -- happens after, because the
      // dispatcher is the queue every other debug command waits behind.
      var plan = dbg.RunOnDbg(() => Plan(dbg, pid, address));
      return WriteDump(Resolve(plan, address), outputPath);
    }

    /// <summary>
    /// Locates the module and, for anything with a real image, reads it. Runs on the debugger
    /// dispatcher — so it deliberately does NOT call <c>GetRawModuleBytes</c>: that marshals itself
    /// onto the CorDebug *engine* thread through an unbounded <c>Dispatcher.Invoke</c>, and calling it
    /// from here would park the manager dispatcher on the engine thread with no timeout, which is the
    /// exact wedge this release exists to fix. <see cref="Resolve"/> makes that call off-dispatcher.
    /// </summary>
    /// <param name="dbg">The debugger services.</param>
    /// <param name="pid">Process to search, or null for the current one.</param>
    /// <param name="address">The module's load address.</param>
    /// <returns>What to dump and how.</returns>
    private static DumpPlan Plan(DebuggerServices dbg, long? pid, ulong address) {
      var process = pid is null
          ? dbg.CurrentProcess
              ?? throw new CommandException(Wire.ErrDbg, "No process is being debugged.")
          : dbg.FindProcess((int)pid.Value)
              ?? throw new CommandException(Wire.ErrDbg, $"No such process: {pid.Value}");

      var module = FindModule(process, address)
          ?? throw new CommandException(
              Wire.ErrDbg,
              $"No module is loaded at 0x{address:X} in process {process.Id}. " +
              "DBG_LIST_MODULES reports each module's load address.");

      // A dynamic (Reflection.Emit) module has no PE image to read at all, but dnSpy can rebuild one
      // from the runtime's own metadata over ICorDebug -- and that is the only path whose method
      // bodies come from the runtime rather than from whatever happens to be in the image.
      if (module.IsDynamic) {
        var dotNetRuntime = module.Runtime.GetDotNetInternalRuntime() as IDbgDotNetRuntime;
        return new DumpPlan(module, dotNetRuntime, null, default);
      }

      if (module.Size == 0) {
        throw new CommandException(
            Wire.ErrDbg, $"The module at 0x{address:X} reports a zero size; nothing to read.");
      }
      if (module.Size > MaxDumpBytes) {
        throw new CommandException(
            Wire.ErrDbg,
            $"The module at 0x{address:X} is {module.Size} bytes, over the {MaxDumpBytes}-byte dump " +
            "limit. Read it in pieces with DBG_READ_MEMORY instead.");
      }

      // ReadMemory allocates the whole buffer and zero-fills any page it cannot read, with no
      // partial-read signal at all -- so a dump can silently contain holes. The dnlib round-trip in
      // WriteDump is the only structural check available.
      byte[] bytes = process.ReadMemory(module.Address, (int)module.Size);
      return new DumpPlan(module, null, bytes, DnlibLayout(module));
    }

    /// <summary>Turns a plan into bytes, making the off-dispatcher metadata call if there is one.</summary>
    /// <param name="plan">What <see cref="Plan"/> decided.</param>
    /// <param name="address">The module's load address, for error messages.</param>
    /// <returns>The bytes to write, with their layout and how they were obtained.</returns>
    private static DumpSource Resolve(DumpPlan plan, ulong address) {
      if (plan.Bytes is not null) {
        return new DumpSource(plan.Bytes, plan.Layout, StrategyMemory);
      }
      if (plan.DotNetRuntime is null) {
        throw new CommandException(
            Wire.ErrDbg,
            $"The module at 0x{address:X} is a dynamic module and this runtime does not expose a " +
            ".NET metadata interface, so there is nothing to dump.");
      }

      var raw = RunBounded(
          () => plan.DotNetRuntime.GetRawModuleBytes(plan.Module), CordebugTimeout,
          "rebuilding the module's metadata");
      if (raw.RawBytes is null || raw.RawBytes.Length == 0) {
        throw new CommandException(
            Wire.ErrDbg,
            $"dnSpy produced no metadata for the dynamic module at 0x{address:X}.");
      }
      return new DumpSource(
          raw.RawBytes, raw.IsFileLayout ? ImageLayout.File : ImageLayout.Memory, StrategyMetadata);
    }

    /// <summary>
    /// Runs work on the thread pool with a bounded wait, for dnSpy APIs that marshal themselves onto
    /// the CorDebug engine thread with no timeout of their own. An abandoned task keeps running
    /// harmlessly on the pool; what matters is that neither the ZMQ poller nor the debugger
    /// dispatcher is left blocked on it forever.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The work to run.</param>
    /// <param name="timeout">How long to wait for it.</param>
    /// <param name="what">What we were doing, for the timeout message.</param>
    /// <returns>The result.</returns>
    private static T RunBounded<T>(Func<T> func, TimeSpan timeout, string what) {
      var task = Task.Run(func);
      if (!task.Wait(timeout)) {
        throw new CommandException(
            Wire.ErrDbgBusy,
            $"dnSpy's debugger engine did not answer within {timeout.TotalSeconds:0}s while {what}.");
      }
      return task.GetAwaiter().GetResult();
    }

    /// <summary>Finds a loaded module by its load address, across every runtime in the process.</summary>
    /// <param name="process">The debugged process.</param>
    /// <param name="address">The load address to match.</param>
    /// <returns>The module, or null if nothing is loaded there.</returns>
    private static DbgModule? FindModule(DbgProcess process, ulong address) {
      foreach (var runtime in process.Runtimes) {
        foreach (var module in runtime.Modules) {
          if (module.Address == address) {
            return module;
          }
        }
      }
      return null;
    }

    /// <summary>
    /// Translates dnSpy's layout flag to dnlib's. The two enums line up by name, and dnSpy's own
    /// readers use exactly this mapping (a non-dynamic in-memory module is a flat *file* image, e.g.
    /// from Assembly.Load(byte[]); anything else is section-mapped). Unknown falls back to Memory,
    /// matching dnSpy's own else-branch.
    /// </summary>
    /// <param name="module">The module whose layout to translate.</param>
    /// <returns>The dnlib image layout to parse the bytes with.</returns>
    private static ImageLayout DnlibLayout(DbgModule module) =>
        module.ImageLayout == DbgImageLayout.File ? ImageLayout.File : ImageLayout.Memory;

    /// <summary>
    /// Writes the bytes out, round-tripping them through dnlib when possible so the result is a
    /// structurally valid PE rather than a verbatim copy of process memory, and counting method
    /// bodies whose RVA lands outside the image. Runs off the debugger dispatcher.
    /// </summary>
    /// <param name="source">The bytes read from the debuggee.</param>
    /// <param name="outputPath">Where to write them.</param>
    /// <returns>The DBG_DUMP_MODULE result row.</returns>
    private static object WriteDump(DumpSource source, string outputPath) {
      int methodsTotal = 0;
      int methodsOutside = 0;
      bool isComplete = false;
      string? rebuildError = null;

      ModuleDefMD? module = null;
      try {
        var peImage = new PEImage(source.Bytes, source.Layout, verify: false);
        try {
          module = ModuleDefMD.Load(peImage);
        } catch (Exception) {
          peImage.Dispose();
          throw;
        }
        CountMethodRvas(module, peImage, out methodsTotal, out methodsOutside);
        module.Write(outputPath);
        isComplete = true;
      } catch (Exception ex) {
        rebuildError = ex.Message;
      } finally {
        module?.Dispose();
      }

      if (!isComplete) {
        // Still give the caller the bytes -- a dump dnlib cannot parse is exactly the interesting
        // case, and an external tool may do better. WriteAllBytes truncates, so a half-written
        // rebuild is replaced rather than left behind.
        try {
          File.WriteAllBytes(outputPath, source.Bytes);
        } catch (Exception ex) {
          throw new CommandException(
              Wire.ErrSaveFailed, $"Failed to write the dump to {outputPath}: {ex.Message}");
        }
      }

      long byteCount = new FileInfo(outputPath).Length;
      return new object?[] {
        outputPath, source.Strategy, source.Layout.ToString(), byteCount, isComplete,
        (long)methodsTotal, (long)methodsOutside,
        BuildWarning(isComplete, rebuildError, methodsTotal, methodsOutside),
      };
    }

    /// <summary>
    /// Counts method bodies whose RVA falls outside every section of the image. This is the
    /// anti-tamper tell: protectors such as ConfuserEx decrypt the bodies into separately allocated
    /// memory and rewrite the metadata RVAs to point there, so the tables survive a dump but the IL
    /// does not. Only the RVA column is read, never the body, so an unreadable body cannot throw.
    /// </summary>
    /// <param name="module">The loaded module.</param>
    /// <param name="peImage">The image it was loaded from, for its section headers.</param>
    /// <param name="total">Receives the number of methods that have a body RVA at all.</param>
    /// <param name="outside">Receives how many of those point outside the image.</param>
    private static void CountMethodRvas(
        ModuleDefMD module, IPEImage peImage, out int total, out int outside) {
      total = 0;
      outside = 0;
      var sections = peImage.ImageSectionHeaders;
      foreach (var type in module.GetTypes()) {
        foreach (var method in type.Methods) {
          uint rva = (uint)method.RVA;
          if (rva == 0) {
            continue;  // abstract, extern, or an encrypted body dnlib recorded as absent
          }
          total++;
          if (!InAnySection(sections, rva)) {
            outside++;
          }
        }
      }
    }

    /// <summary>Whether an RVA falls inside any section's virtual range.</summary>
    /// <param name="sections">The image's section headers.</param>
    /// <param name="rva">The RVA to test.</param>
    /// <returns>True if some section covers it.</returns>
    private static bool InAnySection(IList<ImageSectionHeader> sections, uint rva) {
      foreach (var section in sections) {
        uint start = (uint)section.VirtualAddress;
        uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
        if (rva >= start && rva - start < size) {
          return true;
        }
      }
      return false;
    }

    /// <summary>Builds the human-readable caveat, or null when the dump looks clean.</summary>
    /// <param name="isComplete">Whether dnlib round-tripped the image.</param>
    /// <param name="rebuildError">Why it did not, when it did not.</param>
    /// <param name="methodsTotal">Methods with a body RVA.</param>
    /// <param name="methodsOutside">How many of those point outside the image.</param>
    /// <returns>The warning text, or null.</returns>
    private static string? BuildWarning(
        bool isComplete, string? rebuildError, int methodsTotal, int methodsOutside) {
      if (!isComplete) {
        return "dnlib could not parse the dumped bytes, so they were written verbatim and are not a " +
            $"valid .NET image on their own: {rebuildError}";
      }
      if (methodsOutside > 0) {
        return $"{methodsOutside} of {methodsTotal} method bodies have an RVA outside every section " +
            "of the image. That is the anti-tamper signature: the bodies were decrypted into " +
            "separately allocated memory and the metadata RVAs rewritten to point there, so the " +
            "metadata in this dump is real but the IL for those methods is not. Follow the RVAs with " +
            "DBG_READ_MEMORY to recover them.";
      }
      return null;
    }

    /// <summary>
    /// What to dump, decided on the dispatcher. Either <see cref="Bytes"/> is already populated (a
    /// module with a real image) or <see cref="DotNetRuntime"/> is, meaning the bytes still have to be
    /// rebuilt off-dispatcher.
    /// </summary>
    private readonly struct DumpPlan {
      public DumpPlan(
          DbgModule module, IDbgDotNetRuntime? dotNetRuntime, byte[]? bytes, ImageLayout layout) {
        Module = module;
        DotNetRuntime = dotNetRuntime;
        Bytes = bytes;
        Layout = layout;
      }
      public DbgModule Module { get; }
      public IDbgDotNetRuntime? DotNetRuntime { get; }
      public byte[]? Bytes { get; }
      public ImageLayout Layout { get; }
    }

    /// <summary>Bytes read out of a debuggee, with how they were obtained and how they are laid out.</summary>
    private readonly struct DumpSource {
      public DumpSource(byte[] bytes, ImageLayout layout, string strategy) {
        Bytes = bytes;
        Layout = layout;
        Strategy = strategy;
      }
      public byte[] Bytes { get; }
      public ImageLayout Layout { get; }
      public string Strategy { get; }
    }
  }
}
