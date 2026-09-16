using System;
using System.Collections.Generic;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Reads/writes dnSpy settings by section GUID + attribute key. Values are string ("sz") or
  /// uint ("uint"); dnSpy persists them to its settings XML on exit.
  /// </summary>
  internal static class SettingsCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqReadSetting] = a => ReadSetting(ctx, a);
      t[Wire.ReqWriteSetting] = a => WriteSetting(ctx, a);
    }

    // Args: sectionGuid, key, kind("sz"|"uint"). Returns the value (uint as a wire int), or the
    // type default (0 / null) if the attribute isn't present.
    private static object? ReadSetting(CommandContext ctx, object[] args) {
      Guid section = ParseGuid(CommandContext.GetString(args, 0, "sectionGuid"));
      string key = CommandContext.GetString(args, 1, "key");
      string kind = CommandContext.GetString(args, 2, "kind").ToLowerInvariant();
      return ctx.RunOnUI<object?>(() => {
        var sec = ctx.SettingsService.GetOrCreateSection(section);
        return kind switch {
          "uint" => (object?)(long)sec.Attribute<uint>(key),
          "sz" => sec.Attribute<string>(key),
          _ => throw new CommandException(Wire.ErrBadArgs, $"Unknown setting kind: {kind} (use 'sz' or 'uint')."),
        };
      });
    }

    // Args: sectionGuid, key, kind("sz"|"uint"), value.
    private static object WriteSetting(CommandContext ctx, object[] args) {
      Guid section = ParseGuid(CommandContext.GetString(args, 0, "sectionGuid"));
      string key = CommandContext.GetString(args, 1, "key");
      string kind = CommandContext.GetString(args, 2, "kind").ToLowerInvariant();
      return ctx.RunOnUI<object>(() => {
        var sec = ctx.SettingsService.GetOrCreateSection(section);
        switch (kind) {
          case "uint":
            sec.Attribute(key, unchecked((uint)CommandContext.GetInt64(args, 3, "value")));
            break;
          case "sz":
            sec.Attribute(key, CommandContext.GetString(args, 3, "value"));
            break;
          default:
            throw new CommandException(Wire.ErrBadArgs, $"Unknown setting kind: {kind} (use 'sz' or 'uint').");
        }
        return true;
      });
    }

    private static Guid ParseGuid(string s) =>
        Guid.TryParse(s, out var g)
            ? g
            : throw new CommandException(Wire.ErrBadArgs, $"Invalid section GUID: {s}");
  }
}
