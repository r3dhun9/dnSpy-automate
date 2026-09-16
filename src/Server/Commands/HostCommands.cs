using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>App-level host control: quit dnSpy and refresh its tree/tab views.</summary>
  internal static class HostCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqQuit] = _ => Quit(ctx);
      t[Wire.ReqGuiRefresh] = a => GuiRefresh(ctx, a);
    }

    private static object Quit(CommandContext ctx) {
      // Schedule shutdown asynchronously (Background priority) so this command's reply flushes to
      // the client before the app tears down. Return immediately.
      ctx.AppWindow.MainWindow.Dispatcher.BeginInvoke(
          DispatcherPriority.Background,
          new Action(() => Application.Current?.Shutdown()));
      return true;
    }

    // Args: documentFilename? — refresh that document's tabs; omitted → redraw the whole tree.
    private static object GuiRefresh(CommandContext ctx, object[] args) {
      string? filename = CommandContext.GetOptString(args, 0);
      return ctx.RunOnUI<object>(() => {
        if (!string.IsNullOrEmpty(filename)) {
          var doc = ctx.FindDocument(filename!)
              ?? throw new CommandException(Wire.ErrNotFound, $"Document not loaded: {filename}");
          ctx.TabService.RefreshModifiedDocument(doc);
        } else {
          ctx.TabService.DocumentTreeView.TreeView.RefreshAllNodes();
        }
        return true;
      });
    }
  }
}
