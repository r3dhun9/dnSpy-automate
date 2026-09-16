using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Headless module save via dnlib's writers. dnSpy's own save paths funnel through a modal
  /// dialog (and the real writer is internal), so we write the module directly to disk.
  /// </summary>
  internal static class SaveCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqSaveModule] = a => SaveModule(ctx, a);
    }

    private static object SaveModule(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string outputPath = CommandContext.GetString(args, 1, "outputPath");
      bool useNative = CommandContext.GetOptBool(args, 2, false);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        try {
          Write(module, outputPath, useNative, keepOldMaxStack: false);
        } catch (Exception first) {
          // dnlib recomputes each method's max-stack when it writes, and obfuscated or
          // anti-tampered bodies routinely defeat that calculation — which is the whole point
          // of the protection. dnlib's own advice for that case is KeepOldMaxStack, which
          // writes the recorded values through instead of deriving them. Only used as a
          // fallback: for a module we have edited, recomputing is the correct behaviour.
          try {
            Write(module, outputPath, useNative, keepOldMaxStack: true);
          } catch (Exception second) {
            throw new CommandException(
                Wire.ErrSaveFailed,
                $"Failed to save module: {first.Message} " +
                $"(retry with KeepOldMaxStack also failed: {second.Message})");
          }
        }
        return true;
      });
    }

    /// <summary>Writes a module with dnlib, optionally preserving recorded max-stack values.</summary>
    /// <param name="module">The module to write.</param>
    /// <param name="outputPath">Where to write it.</param>
    /// <param name="useNative">Patch the original image instead of rebuilding it.</param>
    /// <param name="keepOldMaxStack">Write recorded max-stack values rather than recomputing.</param>
    private static void Write(
        ModuleDef module, string outputPath, bool useNative, bool keepOldMaxStack) {
      var flags = keepOldMaxStack ? MetadataFlags.KeepOldMaxStack : 0;
      if (useNative && module is ModuleDefMD moduleMD) {
        var options = new NativeModuleWriterOptions(moduleMD, optimizeImageSize: true);
        options.MetadataOptions.Flags |= flags;
        moduleMD.NativeWrite(outputPath, options);
        return;
      }
      var managed = new ModuleWriterOptions(module);
      managed.MetadataOptions.Flags |= flags;
      module.Write(outputPath, managed);
    }
  }
}
