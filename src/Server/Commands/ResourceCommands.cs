using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Resources;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Embedded-resource enumeration/reading and string-literal (ldstr) scanning.</summary>
  internal static class ResourceCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqListResources] = a => ListResources(ctx, a);
      t[Wire.ReqReadResource] = a => ReadResource(ctx, a);
      t[Wire.ReqListResourceElements] = a => ListResourceElements(ctx, a);
      t[Wire.ReqReadResourceElement] = a => ReadResourceElement(ctx, a);
      t[Wire.ReqListStrings] = a => ListStrings(ctx, a);
    }

    private static object ListResources(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var rows = module.Resources.Select(r => (object)new object?[] {
          r.Name?.String ?? string.Empty,
          r.ResourceType.ToString(),
          r is EmbeddedResource er ? (long)er.Length : (object?)null,
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ReadResource(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string name = CommandContext.GetString(args, 1, "name");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var res = module.Resources.FirstOrDefault(r => r.Name == name);
        if (res is not EmbeddedResource er) {
          throw new CommandException(Wire.ErrNotFound,
              $"No embedded resource named '{name}' (linked resources carry no inline data).");
        }
        return er.CreateReader().ToArray();
      });
    }

    private static object ListResourceElements(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string name = CommandContext.GetString(args, 1, "resourceName");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var res = module.Resources.FirstOrDefault(r => r.Name == name);
        if (res is not EmbeddedResource er) {
          throw new CommandException(Wire.ErrNotFound, $"No embedded resource named '{name}'.");
        }
        if (!ResourceReader.CouldBeResourcesFile(er.CreateReader())) {
          throw new CommandException(Wire.ErrBadArgs,
              $"Resource '{name}' is not a managed .resources set.");
        }
        ResourceElementSet set;
        try {
          set = ResourceReader.Read(module, er.CreateReader());
        } catch (Exception ex) {
          throw new CommandException(Wire.ErrInternal, $"Failed to read resource set: {ex.Message}");
        }
        var rows = set.ResourceElements.Select(e => (object)new object?[] {
          e.Name,
          e.ResourceData?.GetType().Name,
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ReadResourceElement(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string name = CommandContext.GetString(args, 1, "resourceName");
      string elementName = CommandContext.GetString(args, 2, "elementName");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var res = module.Resources.FirstOrDefault(r => r.Name == name);
        if (res is not EmbeddedResource er) {
          throw new CommandException(Wire.ErrNotFound, $"No embedded resource named '{name}'.");
        }
        if (!ResourceReader.CouldBeResourcesFile(er.CreateReader())) {
          throw new CommandException(Wire.ErrBadArgs,
              $"Resource '{name}' is not a managed .resources set.");
        }
        ResourceElementSet set;
        try {
          set = ResourceReader.Read(module, er.CreateReader());
        } catch (Exception ex) {
          throw new CommandException(Wire.ErrInternal, $"Failed to read resource set: {ex.Message}");
        }
        var element = set.ResourceElements.FirstOrDefault(e => e.Name == elementName)
            ?? throw new CommandException(Wire.ErrNotFound, $"No resource element named '{elementName}'.");
        // Row: [code, value] — built-in primitives/strings carry their value; serialized data
        // carries its raw bytes.
        return element.ResourceData switch {
          BuiltInResourceData b => new object?[] { b.Code.ToString(), BuiltInToWire(b.Data) },
          BinaryResourceData bin => new object?[] { bin.Code.ToString(), bin.Data },
          _ => new object?[] { element.ResourceData?.GetType().Name, null },
        };
      });
    }

    /// <summary>Coerces a built-in resource value to a MessagePack-serializable form.</summary>
    private static object? BuiltInToWire(object? data) => data switch {
      null => null,
      string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or byte[] => data,
      _ => data.ToString(),
    };

    private static object ListStrings(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string? filter = CommandContext.GetOptString(args, 1);
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var rows = new List<object>();
        foreach (var type in module.GetTypes()) {
          foreach (var method in type.Methods) {
            if (method.Body is not CilBody body) {
              continue;
            }
            foreach (var instr in body.Instructions) {
              if (instr.OpCode.Code != Code.Ldstr || instr.Operand is not string s) {
                continue;
              }
              if (filter is not null &&
                  s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) {
                continue;
              }
              rows.Add(new object?[] { (long)method.MDToken.Raw, (long)instr.Offset, s });
            }
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }
  }
}
