using System;
using System.Collections.Generic;
using System.Linq;
using dnSpyAutomate.Server.Commands;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Owns the command dispatch table and routes decoded requests to handlers. The table is
  /// populated by asking each command module to register its entries; the actual handler
  /// logic lives in the modules under <c>Commands/</c>.
  ///
  /// Response shapes follow the wire contract: structs are positional arrays whose element
  /// order is fixed. Errors surface as <see cref="CommandException"/> and are converted to
  /// ["XERROR_...", message] tuples here.
  /// </summary>
  internal sealed class CommandHandlers {
    private readonly Dictionary<string, Func<object[], object?>> _table;

    /// <summary>Builds the dispatch table from all command modules.</summary>
    /// <param name="ctx">Shared services + helpers passed to every module.</param>
    public CommandHandlers(CommandContext ctx) {
      _table = new Dictionary<string, Func<object[], object?>>(StringComparer.Ordinal);
      InfraCommands.Register(_table, ctx);
      DocumentCommands.Register(_table, ctx);
      MetadataCommands.Register(_table, ctx);
      DecompileCommands.Register(_table, ctx);
      IlCommands.Register(_table, ctx);
      XrefCommands.Register(_table, ctx);
      ResourceCommands.Register(_table, ctx);
      SearchCommands.Register(_table, ctx);
      BamlCommands.Register(_table, ctx);
      OutputCommands.Register(_table, ctx);
      TreeTabCommands.Register(_table, ctx);
      SaveCommands.Register(_table, ctx);
      EditCommands.Register(_table, ctx);
      HostCommands.Register(_table, ctx);
      SettingsCommands.Register(_table, ctx);
      // Debugger modules
      DebugSessionCommands.Register(_table, ctx);
      DebugExecutionCommands.Register(_table, ctx);
      DebugBreakpointCommands.Register(_table, ctx);
      DebugModuleBreakpointCommands.Register(_table, ctx);
      DebugInspectionCommands.Register(_table, ctx);
      DebugEvalCommands.Register(_table, ctx);
      DebugDumpCommands.Register(_table, ctx);
      _table[Wire.ReqBatch] = Batch;
    }

    /// <summary>
    /// Runs a list of sub-requests in one round-trip. Each element is a full request frame
    /// (e.g. <c>[cmd, arg...]</c>); results are returned positionally, one per item. A failing
    /// sub-command becomes its own <c>["XERROR_...", msg]</c> tuple (via <see cref="Dispatch"/>)
    /// and does not abort the rest of the batch.
    /// </summary>
    /// <param name="items">The sub-request frames.</param>
    /// <returns>An array of per-item response values.</returns>
    private object Batch(object[] items) {
      var results = new object?[items.Length];
      for (int i = 0; i < items.Length; i++) {
        results[i] = Dispatch(items[i]);
      }
      return results;
    }

    /// <summary>
    /// Dispatches a decoded request to its handler and returns the response value.
    /// Handles the bare "PING" string, unknown commands, and error conversion.
    /// </summary>
    /// <param name="request">The decoded request (a string, or object[] of [cmd, args...]).</param>
    /// <returns>The response value to serialize (may be an error tuple).</returns>
    public object? Dispatch(object? request) {
      if (request is string bare) {
        return bare == Wire.Ping
            ? Wire.Pong
            : Wire.Error(Wire.ErrUnknown, "Could not understand input");
      }

      if (request is not object[] arr || arr.Length == 0 || arr[0] is not string cmd) {
        return Wire.Error(Wire.ErrUnknown, "Could not understand input");
      }

      if (!_table.TryGetValue(cmd, out var handler)) {
        return Wire.Error(Wire.ErrUnknown, $"Unknown command: {cmd}");
      }

      var args = arr.Skip(1).ToArray();
      try {
        return handler(args);
      } catch (CommandException ex) {
        return Wire.Error(ex.Code, ex.Message);
      } catch (UiUnavailableException ex) {
        return Wire.Error(Wire.ErrUiUnavailable, ex.Message);
      } catch (Exception ex) {
        return Wire.Error(Wire.ErrInternal, ex.Message);
      }
    }
  }
}
