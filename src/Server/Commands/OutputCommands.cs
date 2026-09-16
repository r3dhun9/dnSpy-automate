using System;
using System.Collections.Generic;
using dnSpy.Contracts.Text;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Output-pane write/read/clear via <see cref="dnSpy.Contracts.Output.IOutputService"/>.</summary>
  internal static class OutputCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqOutputWrite] = a => OutputWrite(ctx, a);
      t[Wire.ReqOutputRead] = a => OutputRead(ctx, a);
      t[Wire.ReqOutputClear] = a => OutputClear(ctx, a);
    }

    private static object OutputWrite(CommandContext ctx, object[] args) {
      var guid = ParseGuid(CommandContext.GetString(args, 0, "paneGuid"));
      string name = CommandContext.GetString(args, 1, "paneName");
      string text = CommandContext.GetString(args, 2, "text");
      return ctx.RunOnUI<object>(() => {
        // Use the (Guid, string, string) overload so we don't depend on IContentType
        // (which lives in an unreferenced assembly); "text" is the plain-text content type.
        var pane = ctx.OutputService.Create(guid, name, "text");
        // IOutputTextPane inherits two Write(color,text) families; the (TextColor, string)
        // form is unambiguous.
        pane.Write(TextColor.ReplScriptOutputText, text + Environment.NewLine);
        return true;
      });
    }

    private static object OutputRead(CommandContext ctx, object[] args) {
      var guid = ParseGuid(CommandContext.GetString(args, 0, "paneGuid"));
      return ctx.RunOnUI<object>(() => {
        var pane = ctx.OutputService.GetTextPane(guid)
            ?? throw new CommandException(Wire.ErrNotFound, "No output pane with that guid.");
        return pane.GetText();
      });
    }

    private static object OutputClear(CommandContext ctx, object[] args) {
      var guid = ParseGuid(CommandContext.GetString(args, 0, "paneGuid"));
      return ctx.RunOnUI<object>(() => {
        var pane = ctx.OutputService.GetTextPane(guid);
        if (pane is null) {
          return false;
        }
        pane.Clear();
        return true;
      });
    }

    private static Guid ParseGuid(string value) {
      if (!Guid.TryParse(value, out var guid)) {
        throw new CommandException(Wire.ErrBadArgs, $"Invalid pane guid: {value}");
      }
      return guid;
    }
  }
}
