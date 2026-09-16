using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Documents.Tabs;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Assembly-tree navigation, tab listing, and active-tab content.</summary>
  internal static class TreeTabCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqNavigateTo] = a => NavigateTo(ctx, a);
      t[Wire.ReqListTabs] = a => ListTabs(ctx, a);
      t[Wire.ReqGetActiveTabText] = _ => GetActiveTabText(ctx);
    }

    private static object NavigateTo(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var member = ctx.ResolveTokenLive(module, token)
            ?? throw new CommandException(Wire.ErrNotFound, $"Token 0x{token:X8} does not resolve.");
        var node = ctx.TabService.DocumentTreeView.FindNode(member);
        ctx.TabService.FollowReference(node ?? (object)member, newTab: false, setFocus: true, onShown: null);
        return true;
      });
    }

    private static object ListTabs(CommandContext ctx, object[] args) {
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return ctx.RunOnUI<object>(() => {
        var active = ctx.TabService.ActiveTab;
        var tabs = ctx.TabService.SortedTabs.ToList();
        var rows = new List<object>();
        for (int i = 0; i < tabs.Count; i++) {
          rows.Add(new object?[] { (long)i, tabs[i].Content?.Title, ReferenceEquals(tabs[i], active) });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object GetActiveTabText(CommandContext ctx) => ctx.RunOnUI<object>(() => {
      var tab = ctx.TabService.ActiveTab
          ?? throw new CommandException(Wire.ErrNotFound, "No active tab.");
      var viewer = tab.TryGetDocumentViewer()
          ?? throw new CommandException(Wire.ErrNotFound, "Active tab has no document viewer.");
      return viewer.Content?.Text ?? string.Empty;
    });
  }
}
