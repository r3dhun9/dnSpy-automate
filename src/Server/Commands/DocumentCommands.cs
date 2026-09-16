using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnSpy.Contracts.Documents;
using dnlib.DotNet;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Loaded-document listing/loading and decompiler enumeration + basic decompile.</summary>
  internal static class DocumentCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqListDocuments] = a => ListDocuments(ctx, a);
      t[Wire.ReqLoadDocument] = a => LoadDocument(ctx, a);
      t[Wire.ReqUnloadDocument] = a => UnloadDocument(ctx, a);
      t[Wire.ReqReloadDocument] = a => ReloadDocument(ctx, a);
      t[Wire.ReqGetModuleInfo] = a => GetModuleInfo(ctx, a);
      t[Wire.ReqListAssemblyRefs] = a => ListAssemblyRefs(ctx, a);
      t[Wire.ReqListDecompilers] = a => ListDecompilers(ctx, a);
      t[Wire.ReqListTypes] = a => ListTypes(ctx, a);
      t[Wire.ReqDecompileType] = a => Decompile(ctx, a, DecompileTarget.Type);
      t[Wire.ReqDecompileMethod] = a => Decompile(ctx, a, DecompileTarget.Method);
    }

    private static object ListDocuments(CommandContext ctx, object[] args) {
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return ctx.RunOnUI<object>(() => {
        var rows = ctx.DocumentService.GetDocuments().Select(d => (object)DocumentRow(d));
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object LoadDocument(CommandContext ctx, object[] args) {
      string path = CommandContext.GetString(args, 0, "path");
      return ctx.RunOnUI<object>(() => {
        if (!File.Exists(path)) {
          throw new CommandException(Wire.ErrBadLoad, $"File not found: {path}");
        }
        var info = DsDocumentInfo.CreateDocument(path);
        var doc = ctx.DocumentService.TryGetOrCreate(info, isAutoLoaded: false);
        if (doc is null) {
          throw new CommandException(Wire.ErrBadLoad, $"Failed to load document: {path}");
        }
        if (doc.ModuleDef is null) {
          ctx.DocumentService.Remove(doc.Key);
          throw new CommandException(Wire.ErrBadLoad, $"Not a .NET module: {path}");
        }
        return DocumentRow(doc);
      });
    }

    private static object UnloadDocument(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "filename");
      return ctx.RunOnUI<object>(() => {
        var doc = ctx.FindDocument(filename);
        if (doc is null) {
          return false;
        }
        ctx.DocumentService.Remove(doc.Key);
        return true;
      });
    }

    private static object ReloadDocument(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "filename");
      return ctx.RunOnUI<object>(() => {
        var doc = ctx.FindDocument(filename)
            ?? throw new CommandException(Wire.ErrNotFound, $"Document not loaded: {filename}");
        string path = doc.Filename
            ?? throw new CommandException(Wire.ErrBadArgs, "Document has no on-disk path to reload from.");
        ctx.DocumentService.Remove(doc.Key);
        var reloaded = ctx.DocumentService.TryGetOrCreate(
            DsDocumentInfo.CreateDocument(path), isAutoLoaded: false)
            ?? throw new CommandException(Wire.ErrBadLoad, $"Failed to reload document: {path}");
        return DocumentRow(reloaded);
      });
    }

    private static object GetModuleInfo(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      return ctx.RunOnUI<object>(() => {
        var doc = ctx.FindDocument(filename)
            ?? throw new CommandException(Wire.ErrNotFound, $"Document not loaded: {filename}");
        var module = doc.ModuleDef
            ?? throw new CommandException(Wire.ErrNotFound, $"Not a .NET module: {filename}");
        var asm = doc.AssemblyDef;
        return new object?[] {
          module.EntryPoint is MethodDef ep ? (long)ep.MDToken.Raw : (object?)null,
          module.RuntimeVersion,
          TargetFramework(asm),
          module.Mvid?.ToString(),
          module.Machine.ToString(),
          module.Is32BitRequired,
          module.Is32BitPreferred,
          module.Kind.ToString(),
          asm?.Version?.ToString(),
          asm?.Culture?.String,
          ToHex(asm?.PublicKey?.Token?.Data),
          module.Kind == ModuleKind.Console || module.Kind == ModuleKind.Windows,
        };
      });
    }

    private static object ListAssemblyRefs(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(filename);
        var rows = module.GetAssemblyRefs().Select(ar => (object)new object?[] {
          ar.Name?.String,
          ar.Version?.ToString(),
          ar.Culture?.String,
          ToHex(ar.PublicKeyOrToken?.Data),
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListDecompilers(CommandContext ctx, object[] args) {
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return ctx.RunOnUI<object>(() => {
        var rows = new List<object>();
        foreach (var d in ctx.DecompilerService.AllDecompilers) {
          rows.Add(new object[] { d.UniqueGuid.ToString(), d.GenericNameUI, d.UniqueNameUI, d.OrderUI });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListTypes(CommandContext ctx, object[] args) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(filename);
        var rows = module.GetTypes().Select(type => (object)CommandContext.TypeRow(type));
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private enum DecompileTarget { Type, Method }

    private static object Decompile(CommandContext ctx, object[] args, DecompileTarget target) {
      string filename = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      string? langGuid = CommandContext.GetOptString(args, 2);

      return ctx.RunOnUI<object>(() => {
        var decompiler = ctx.ResolveDecompiler(langGuid);
        var output = new dnSpy.Contracts.Decompiler.StringBuilderDecompilerOutput();
        var dctx = new dnSpy.Contracts.Decompiler.DecompilationContext();

        if (target == DecompileTarget.Type) {
          var typeDef = ctx.ResolveMember<TypeDef>(filename, token, "type");
          decompiler.Decompile(typeDef, output, dctx);
        } else {
          var methodDef = ctx.ResolveMember<MethodDef>(filename, token, "method");
          decompiler.Decompile(methodDef, output, dctx);
        }
        return output.GetText();
      });
    }

    /// <summary>Document row: [filename, shortName, isDotNet, asmFullName?, moduleName?].</summary>
    private static object?[] DocumentRow(IDsDocument doc) => new object?[] {
      doc.Filename ?? string.Empty,
      doc.GetShortName() ?? string.Empty,
      doc.ModuleDef is not null,
      doc.AssemblyDef?.FullName,
      doc.ModuleDef?.Name?.String,
    };

    /// <summary>Reads the assembly's TargetFrameworkAttribute moniker (e.g. ".NETCoreApp,Version=v8.0"), or null.</summary>
    private static string? TargetFramework(AssemblyDef? asm) {
      var ca = asm?.CustomAttributes.Find("System.Runtime.Versioning.TargetFrameworkAttribute");
      if (ca is not null && ca.ConstructorArguments.Count > 0) {
        return ca.ConstructorArguments[0].Value?.ToString();
      }
      return null;
    }

    /// <summary>Lowercase hex of a byte blob (e.g. a public-key token), or null if empty.</summary>
    private static string? ToHex(byte[]? data) =>
        data is null || data.Length == 0
            ? null
            : BitConverter.ToString(data).Replace("-", string.Empty).ToLowerInvariant();
  }
}
