using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Cross-reference queries reimplemented over dnlib (dnSpy's analyzer engine is internal).
  /// Each scans all loaded modules; results are rows [token, fullName, documentFilename]
  /// (callers add the call-site ilOffset; field-access adds the ilOffset and an access-kind).
  /// </summary>
  internal static class XrefCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqFindCallers] = a => FindCallers(ctx, a);
      t[Wire.ReqFindCallees] = a => FindCallees(ctx, a);
      t[Wire.ReqFindFieldAccess] = a => FindFieldAccess(ctx, a);
      t[Wire.ReqFindDerivedTypes] = a => FindDerivedTypes(ctx, a);
      t[Wire.ReqFindImplementors] = a => FindImplementors(ctx, a);
      t[Wire.ReqFindOverrides] = a => FindOverrides(ctx, a);
    }

    private static object FindCallers(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      string? scope = CommandContext.GetOptString(args, 2);
      long offset = CommandContext.GetOptInt64(args, 3, 0);
      long limit = CommandContext.GetOptInt64(args, 4, 0);
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<MethodDef>(file, token, "method");
        var seen = new HashSet<string>();
        var rows = new List<object>();
        foreach (var method in ScopedMethods(ctx, target, scope)) {
          if (method.Body is not CilBody body) {
            continue;
          }
          // One row per call site (not per method) so each carries a navigable IL offset.
          foreach (var instr in body.Instructions) {
            if (instr.Operand is IMethod im && SameMethod(ResolveMethod(im), target) &&
                seen.Add($"{MemberKey(method)}@{instr.Offset:X4}")) {
              rows.Add(new object?[] {
                (long)method.MDToken.Raw, method.FullName, ModuleFile(method), (long)instr.Offset,
              });
            }
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object FindCallees(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<MethodDef>(file, token, "method");
        if (target.Body is not CilBody body) {
          return CommandContext.PageResult(Array.Empty<object>(), offset, limit);
        }
        var seen = new HashSet<string>();
        var rows = new List<object>();
        foreach (var instr in body.Instructions) {
          if (instr.Operand is not IMethod im) {
            continue;
          }
          var def = ResolveMethod(im);
          string key = def is not null ? MemberKey(def) : "ref:" + im.FullName;
          if (!seen.Add(key)) {
            continue;
          }
          rows.Add(def is not null
              ? MemberRow(def)
              : new object?[] { null, im.FullName, null });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object FindFieldAccess(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "fieldToken");
      string mode = CommandContext.GetOptString(args, 2) ?? "both";
      string? scope = CommandContext.GetOptString(args, 3);
      long offset = CommandContext.GetOptInt64(args, 4, 0);
      long limit = CommandContext.GetOptInt64(args, 5, 0);
      bool wantRead = mode is "read" or "both";
      bool wantWrite = mode is "write" or "both";
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<FieldDef>(file, token, "field");
        var rows = new List<object>();
        foreach (var method in ScopedMethods(ctx, target, scope)) {
          if (method.Body is not CilBody body) {
            continue;
          }
          foreach (var instr in body.Instructions) {
            var access = FieldAccessKind(instr.OpCode.Code);
            if (access is null) {
              continue;
            }
            if ((access == "read" && !wantRead) || (access == "write" && !wantWrite)) {
              continue;
            }
            if (instr.Operand is IField ifld && SameField(ResolveField(ifld), target)) {
              rows.Add(new object?[] {
                (long)method.MDToken.Raw, method.FullName, ModuleFile(method), (long)instr.Offset, access,
              });
            }
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object FindDerivedTypes(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      string? scope = CommandContext.GetOptString(args, 2);
      long offset = CommandContext.GetOptInt64(args, 3, 0);
      long limit = CommandContext.GetOptInt64(args, 4, 0);
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<TypeDef>(file, token, "type");
        var rows = new List<object>();
        foreach (var type in ScopedTypes(ctx, target, scope)) {
          if (type.BaseType is not null && SameType(type.BaseType.ResolveTypeDef(), target)) {
            rows.Add(MemberRow(type));
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object FindImplementors(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      string? scope = CommandContext.GetOptString(args, 2);
      long offset = CommandContext.GetOptInt64(args, 3, 0);
      long limit = CommandContext.GetOptInt64(args, 4, 0);
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<TypeDef>(file, token, "type");
        var rows = new List<object>();
        foreach (var type in ScopedTypes(ctx, target, scope)) {
          foreach (var impl in type.Interfaces) {
            if (SameType(impl.Interface?.ResolveTypeDef(), target)) {
              rows.Add(MemberRow(type));
              break;
            }
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object FindOverrides(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      string? scope = CommandContext.GetOptString(args, 2);
      long offset = CommandContext.GetOptInt64(args, 3, 0);
      long limit = CommandContext.GetOptInt64(args, 4, 0);
      return ctx.RunOnUI<object>(() => {
        var target = ctx.ResolveMember<MethodDef>(file, token, "method");
        var rows = new List<object>();
        var seen = new HashSet<string>();
        foreach (var method in ScopedMethods(ctx, target, scope)) {
          bool isOverride = false;

          // Explicit overrides (.override directives).
          foreach (var ov in method.Overrides) {
            if (SameMethod(ResolveMethod(ov.MethodDeclaration), target)) {
              isOverride = true;
              break;
            }
          }

          // Implicit virtual-slot override: same name+signature, virtual, and the declaring
          // type derives from the target's declaring type.
          if (!isOverride && target.IsVirtual && method.IsVirtual &&
              method.Name == target.Name && method != target &&
              new SigComparer().Equals(method.MethodSig, target.MethodSig) &&
              DerivesFrom(method.DeclaringType, target.DeclaringType)) {
            isOverride = true;
          }

          if (isOverride && seen.Add(MemberKey(method))) {
            rows.Add(MemberRow(method));
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    // ---- Scanning helpers ----------------------------------------------------------

    private static IEnumerable<ModuleDef> AllModules(CommandContext ctx) {
      // Dedup: a single-module assembly exposes the same ModuleDef on both the assembly document
      // and its child module node, which would otherwise scan (and emit) every member twice.
      var seen = new HashSet<ModuleDef>(ReferenceEqualityComparer.Instance);
      foreach (var doc in ctx.DocumentService.GetDocuments()) {
        if (doc.ModuleDef is not null && seen.Add(doc.ModuleDef)) {
          yield return doc.ModuleDef;
        }
        foreach (var child in doc.Children) {
          if (child.ModuleDef is not null && seen.Add(child.ModuleDef)) {
            yield return child.ModuleDef;
          }
        }
      }
    }

    private static IEnumerable<TypeDef> AllTypes(CommandContext ctx) =>
        AllModules(ctx).SelectMany(m => m.GetTypes());

    private static IEnumerable<MethodDef> AllMethods(CommandContext ctx) =>
        AllTypes(ctx).SelectMany(t => t.Methods);

    // ---- Scoping (opt-in) ----------------------------------------------------------
    // The scan is O(all loaded modules) by default. An optional trailing "scope" arg lets a
    // client bound the cost: "all"/absent keeps the full scan; "referencing" restricts it to
    // modules that could possibly reference the target. That set is complete for cross-assembly
    // references — any IL referencing a member in another assembly must carry an AssemblyRef to
    // it — plus the target assembly's own modules for intra-assembly references.

    private static IEnumerable<ModuleDef> ScopedModules(
        CommandContext ctx, IMemberDef target, string? scope) {
      if (string.IsNullOrEmpty(scope) || scope!.Equals("all", StringComparison.OrdinalIgnoreCase)) {
        return AllModules(ctx);
      }
      if (scope!.Equals("referencing", StringComparison.OrdinalIgnoreCase)) {
        var asm = target.Module?.Assembly;
        if (asm is null) {
          // No defining assembly (e.g. a bare netmodule); can't prune, so keep the full scan.
          return AllModules(ctx);
        }
        var asmName = asm.Name;
        return AllModules(ctx).Where(m => IsInScope(m, asmName));
      }
      throw new CommandException(Wire.ErrBadArgs, $"Unknown xref scope: {scope}");
    }

    private static bool IsInScope(ModuleDef m, UTF8String asmName) {
      // A module belonging to the target's own (possibly multi-module) assembly.
      if (m.Assembly is not null && NameEquals(m.Assembly.Name, asmName)) {
        return true;
      }
      // A metadata module that references the target's assembly.
      if (m is ModuleDefMD md) {
        return md.GetAssemblyRefs().Any(ar => NameEquals(ar.Name, asmName));
      }
      // Non-metadata module: can't inspect refs cheaply, so don't exclude it.
      return true;
    }

    private static bool NameEquals(UTF8String? a, UTF8String? b) =>
        string.Equals(a?.String, b?.String, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TypeDef> ScopedTypes(
        CommandContext ctx, IMemberDef target, string? scope) =>
        ScopedModules(ctx, target, scope).SelectMany(m => m.GetTypes());

    private static IEnumerable<MethodDef> ScopedMethods(
        CommandContext ctx, IMemberDef target, string? scope) =>
        ScopedTypes(ctx, target, scope).SelectMany(t => t.Methods);

    private static string? FieldAccessKind(Code code) => code switch {
      Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda => "read",
      Code.Stfld or Code.Stsfld => "write",
      _ => null,
    };

    // ---- Resolution / comparison ---------------------------------------------------

    private static MethodDef? ResolveMethod(IMethod? m) => m switch {
      null => null,
      MethodDef md => md,
      MemberRef mr => mr.ResolveMethod() as MethodDef,
      MethodSpec ms => ResolveMethod(ms.Method),
      _ => null,
    };

    private static FieldDef? ResolveField(IField? f) => f switch {
      null => null,
      FieldDef fd => fd,
      MemberRef mr => mr.ResolveField(),
      _ => null,
    };

    private static bool SameMethod(MethodDef? def, MethodDef target) =>
        def is not null && (def == target ||
            (def.MDToken == target.MDToken && SameModule(def.Module, target.Module)));

    private static bool SameField(FieldDef? def, FieldDef target) =>
        def is not null && (def == target ||
            (def.MDToken == target.MDToken && SameModule(def.Module, target.Module)));

    private static bool SameType(TypeDef? def, TypeDef target) =>
        def is not null && (def == target ||
            (def.MDToken == target.MDToken && SameModule(def.Module, target.Module)));

    private static bool SameModule(ModuleDef? a, ModuleDef? b) =>
        ReferenceEquals(a, b) ||
        (a is not null && b is not null && a.Location == b.Location && a.Name == b.Name);

    private static bool DerivesFrom(TypeDef? type, TypeDef? baseType) {
      if (baseType is null) {
        return false;
      }
      var current = type?.BaseType?.ResolveTypeDef();
      var guard = 0;
      while (current is not null && guard++ < 500) {
        if (SameType(current, baseType)) {
          return true;
        }
        current = current.BaseType?.ResolveTypeDef();
      }
      return false;
    }

    // ---- Row helpers ---------------------------------------------------------------

    private static object[] MemberRow(IMemberDef member) => new object[] {
      (long)member.MDToken.Raw,
      member.FullName,
      ModuleFile(member),
    };

    private static string ModuleFile(IMemberDef member) =>
        member.Module?.Location ?? member.Module?.Name?.String ?? string.Empty;

    private static string MemberKey(IMemberDef member) =>
        $"{ModuleFile(member)}#{member.MDToken.Raw:X8}";
  }
}
