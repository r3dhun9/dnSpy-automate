using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnSpy.Contracts.Decompiler;
using dnlib.DotNet;
using dnlib.DotNet.Resources;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// WPF BAML→XAML via the <see cref="IBamlDecompiler"/> contract (optional MEF import).
  /// BAML lives as <c>*.baml</c> entries inside the module's <c>*.g.resources</c> sets.
  /// </summary>
  internal static class BamlCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqListBaml] = a => ListBaml(ctx, a);
      t[Wire.ReqDecompileBaml] = a => DecompileBaml(ctx, a);
    }

    private static object ListBaml(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var names = new List<object>();
        foreach (var (name, _) in EnumerateBaml(module)) {
          names.Add(name);
        }
        return CommandContext.PageResult(names, offset, limit);
      });
    }

    private static object DecompileBaml(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string bamlName = CommandContext.GetString(args, 1, "bamlName");
      return ctx.RunOnUI<object>(() => {
        if (ctx.BamlDecompiler is null) {
          throw new CommandException(Wire.ErrUnavailable, "BAML decompiler extension is not loaded.");
        }
        var module = ctx.ResolveModule(file);
        var match = EnumerateBaml(module)
            .FirstOrDefault(b => string.Equals(b.name, bamlName, StringComparison.OrdinalIgnoreCase));
        if (match.data is null) {
          throw new CommandException(Wire.ErrNotFound, $"BAML resource not found: {bamlName}");
        }

        var options = BamlDecompilerOptions.Create(ctx.DecompilerService.Decompiler);
        var outputOptions = new XamlOutputOptions {
          IndentChars = "  ",
          NewLineChars = "\n",
          NewLineOnAttributes = false,
        };
        using var stream = new MemoryStream();
        ctx.BamlDecompiler.Decompile(
            module, match.data, CancellationToken.None, options, stream, outputOptions);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
      });
    }

    /// <summary>Yields (elementName, bamlBytes) for every *.baml entry in the module.</summary>
    private static IEnumerable<(string name, byte[]? data)> EnumerateBaml(ModuleDef module) {
      foreach (var res in module.Resources.OfType<EmbeddedResource>()) {
        if (!ResourceReader.CouldBeResourcesFile(res.CreateReader())) {
          continue;
        }
        ResourceElementSet set;
        try {
          set = ResourceReader.Read(module, res.CreateReader());
        } catch (Exception) {
          continue;
        }
        foreach (var element in set.ResourceElements) {
          if (element.Name is not null &&
              element.Name.EndsWith(".baml", StringComparison.OrdinalIgnoreCase)) {
            yield return (element.Name, ExtractBytes(element.ResourceData));
          }
        }
      }
    }

    /// <summary>Extracts raw bytes from a resource element's data (byte[] or Stream).</summary>
    private static byte[]? ExtractBytes(IResourceData? data) {
      switch (data) {
        case BinaryResourceData bin:
          return bin.Data;
        case BuiltInResourceData builtIn when builtIn.Data is byte[] bytes:
          return bytes;
        case BuiltInResourceData builtIn when builtIn.Data is Stream stream: {
          using var ms = new MemoryStream();
          stream.Position = 0;
          stream.CopyTo(ms);
          return ms.ToArray();
        }
        default:
          return null;
      }
    }
  }
}
