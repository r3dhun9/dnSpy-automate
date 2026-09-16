using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnlib.DotNet;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Decompilation for the member kinds beyond type/method, whole-module and per-namespace
  /// decompilation, and source↔IL span mapping (via a capturing <see cref="IDecompilerOutput"/>).
  /// </summary>
  internal static class DecompileCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDecompileField] = a => DecompileMember(ctx, a, Kind.Field);
      t[Wire.ReqDecompileProperty] = a => DecompileMember(ctx, a, Kind.Property);
      t[Wire.ReqDecompileEvent] = a => DecompileMember(ctx, a, Kind.Event);
      t[Wire.ReqDecompileModule] = a => DecompileModule(ctx, a);
      t[Wire.ReqDecompileNamespace] = a => DecompileNamespace(ctx, a);
      t[Wire.ReqGetIlMapping] = a => GetIlMapping(ctx, a);
    }

    private enum Kind { Field, Property, Event }

    private static object DecompileMember(CommandContext ctx, object[] args, Kind kind) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      string? lang = CommandContext.GetOptString(args, 2);
      return ctx.RunOnUI<object>(() => {
        var decompiler = ctx.ResolveDecompiler(lang);
        var output = new StringBuilderDecompilerOutput();
        var dctx = new DecompilationContext();
        switch (kind) {
          case Kind.Field:
            decompiler.Decompile(ctx.ResolveMember<FieldDef>(file, token, "field"), output, dctx);
            break;
          case Kind.Property:
            decompiler.Decompile(ctx.ResolveMember<PropertyDef>(file, token, "property"), output, dctx);
            break;
          case Kind.Event:
            decompiler.Decompile(ctx.ResolveMember<EventDef>(file, token, "event"), output, dctx);
            break;
        }
        return output.GetText();
      });
    }

    private static object DecompileModule(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string? lang = CommandContext.GetOptString(args, 1);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var decompiler = ctx.ResolveDecompiler(lang);
        var output = new StringBuilderDecompilerOutput();
        decompiler.Decompile(module, output, new DecompilationContext());
        return output.GetText();
      });
    }

    private static object DecompileNamespace(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string ns = CommandContext.GetString(args, 1, "namespace");
      string? lang = CommandContext.GetOptString(args, 2);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var types = module.Types.Where(t => (t.Namespace?.String ?? string.Empty) == ns).ToArray();
        if (types.Length == 0) {
          throw new CommandException(Wire.ErrNotFound, $"No top-level types in namespace: {ns}");
        }
        var decompiler = ctx.ResolveDecompiler(lang);
        var output = new StringBuilderDecompilerOutput();
        decompiler.DecompileNamespace(ns, types, output, new DecompilationContext());
        return output.GetText();
      });
    }

    private static object GetIlMapping(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      string? lang = CommandContext.GetOptString(args, 2);
      return ctx.RunOnUI<object>(() => {
        var method = ctx.ResolveMember<MethodDef>(file, token, "method");
        var decompiler = ctx.ResolveDecompiler(lang);
        var output = new CapturingDecompilerOutput();
        decompiler.Decompile(method, output, new DecompilationContext { CalculateILSpans = true });

        var info = output.DebugInfos.FirstOrDefault(d => d.Method?.MDToken == method.MDToken)
            ?? output.DebugInfos.FirstOrDefault();
        var statements = (info?.Statements ?? Array.Empty<SourceStatement>())
            .Select(s => (object)new object[] {
              (long)s.ILSpan.Start, (long)s.ILSpan.End,
              (long)s.TextSpan.Start, (long)s.TextSpan.End,
            })
            .ToArray();
        return new object[] { output.GetText(), statements };
      });
    }
  }

  /// <summary>
  /// An <see cref="IDecompilerOutput"/> that accumulates text via an inner
  /// <see cref="StringBuilderDecompilerOutput"/> and captures the <see cref="MethodDebugInfo"/>
  /// the decompiler emits through <see cref="IDecompilerOutput.AddCustomData{TData}"/>
  /// (<see cref="PredefinedCustomDataIds.DebugInfo"/>), which drives source↔IL mapping.
  /// </summary>
  internal sealed class CapturingDecompilerOutput : IDecompilerOutput {
    private readonly StringBuilderDecompilerOutput _inner = new StringBuilderDecompilerOutput();

    /// <summary>The debug-info records captured during decompilation.</summary>
    public List<MethodDebugInfo> DebugInfos { get; } = new List<MethodDebugInfo>();

    public int Length => _inner.Length;
    public int NextPosition => _inner.NextPosition;

    // Must be true or the decompiler skips emitting debug info.
    public bool UsesCustomData => true;

    public void IncreaseIndent() => _inner.IncreaseIndent();
    public void DecreaseIndent() => _inner.DecreaseIndent();
    public void WriteLine() => _inner.WriteLine();
    public void Write(string text, object color) => _inner.Write(text, color);
    public void Write(string text, int index, int length, object color) =>
        _inner.Write(text, index, length, color);
    public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) =>
        _inner.Write(text, reference, flags, color);
    public void Write(string text, int index, int length, object? reference,
        DecompilerReferenceFlags flags, object color) =>
        _inner.Write(text, index, length, reference, flags, color);

    public void AddCustomData<TData>(string id, TData data) {
      if (id == PredefinedCustomDataIds.DebugInfo && data is MethodDebugInfo mdi) {
        DebugInfos.Add(mdi);
      }
    }

    /// <summary>Returns the accumulated decompiled text.</summary>
    public string GetText() => _inner.GetText();
  }
}
