using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Metadata enumeration and member resolution over dnlib. All handlers run inside
  /// <see cref="CommandContext.RunOnUI{T}"/> since they touch the document service + dnlib.
  /// </summary>
  internal static class MetadataCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqListNamespaces] = a => ListNamespaces(ctx, a);
      t[Wire.ReqListNestedTypes] = a => ListNestedTypes(ctx, a);
      t[Wire.ReqListMethods] = a => ListMethods(ctx, a);
      t[Wire.ReqListFields] = a => ListFields(ctx, a);
      t[Wire.ReqListProperties] = a => ListProperties(ctx, a);
      t[Wire.ReqListEvents] = a => ListEvents(ctx, a);
      t[Wire.ReqGetTypeInfo] = a => GetTypeInfo(ctx, a);
      t[Wire.ReqGetMethodInfo] = a => GetMethodInfo(ctx, a);
      t[Wire.ReqListParameters] = a => ListParameters(ctx, a);
      t[Wire.ReqListCustomAttributes] = a => ListCustomAttributes(ctx, a);
      t[Wire.ReqResolveToken] = a => ResolveToken(ctx, a);
      t[Wire.ReqFindType] = a => FindType(ctx, a);
      t[Wire.ReqFindMethod] = a => FindMethod(ctx, a);
    }

    private static object ListNamespaces(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in module.GetTypes()) {
          namespaces.Add(type.Namespace?.String ?? string.Empty);
        }
        return CommandContext.PageResult(namespaces.Cast<object>(), offset, limit);
      });
    }

    private static object ListNestedTypes(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var type = ctx.ResolveMember<TypeDef>(file, token, "type");
        return CommandContext.PageResult(type.NestedTypes.Select(CommandContext.TypeRow), offset, limit);
      });
    }

    private static object ListMethods(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var type = ctx.ResolveMember<TypeDef>(file, token, "type");
        return CommandContext.PageResult(type.Methods.Select(CommandContext.MethodRow), offset, limit);
      });
    }

    private static object ListFields(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var type = ctx.ResolveMember<TypeDef>(file, token, "type");
        var rows = type.Fields.Select(f => (object)new object?[] {
          (long)f.MDToken.Raw,
          f.Name?.String ?? string.Empty,
          f.FieldType?.FullName,
          f.IsStatic,
          (long)f.Attributes,
          ValueToString(f.Constant?.Value),
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListProperties(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var type = ctx.ResolveMember<TypeDef>(file, token, "type");
        var rows = type.Properties.Select(p => (object)new object?[] {
          (long)p.MDToken.Raw,
          p.Name?.String ?? string.Empty,
          p.PropertySig?.RetType?.FullName,
          TokenOrNull(p.GetMethod),
          TokenOrNull(p.SetMethod),
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListEvents(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var type = ctx.ResolveMember<TypeDef>(file, token, "type");
        var rows = type.Events.Select(e => (object)new object?[] {
          (long)e.MDToken.Raw,
          e.Name?.String ?? string.Empty,
          e.EventType?.FullName,
          TokenOrNull(e.AddMethod),
          TokenOrNull(e.RemoveMethod),
        });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object GetTypeInfo(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "typeToken");
      return ctx.RunOnUI<object>(() => {
        var t = ctx.ResolveMember<TypeDef>(file, token, "type");
        var interfaces = t.Interfaces.Select(i => (object?)i.Interface?.FullName).ToArray();
        return new object?[] {
          (long)t.MDToken.Raw,
          t.FullName,
          t.Name?.String ?? string.Empty,
          t.Namespace?.String ?? string.Empty,
          t.BaseType?.FullName,
          interfaces,
          (long)t.Attributes,
          t.IsInterface,
          t.IsEnum,
          t.IsValueType,
          TokenOrNull(t.DeclaringType),
        };
      });
    }

    private static object GetMethodInfo(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      return ctx.RunOnUI<object>(() => {
        var m = ctx.ResolveMember<MethodDef>(file, token, "method");
        var paramRows = m.Parameters
            .Where(p => p.IsNormalMethodParameter)
            .Select(p => (object?)new object?[] { p.Name, p.Type?.FullName })
            .ToArray();
        return new object?[] {
          (long)m.MDToken.Raw,
          m.Name?.String ?? string.Empty,
          m.FullName,
          m.ReturnType?.FullName,
          paramRows,
          m.IsStatic,
          (long)m.Attributes,
          m.HasBody,
        };
      });
    }

    private static object ListParameters(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var m = ctx.ResolveMember<MethodDef>(file, token, "method");
        var rows = m.Parameters
            .Where(p => p.IsNormalMethodParameter)
            .Select(p => (object)new object?[] {
              p.MethodSigIndex,
              p.Name,
              p.Type?.FullName,
              p.ParamDef?.IsIn ?? false,
              p.ParamDef?.IsOut ?? false,
              p.ParamDef?.IsOptional ?? false,
            });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ListCustomAttributes(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      long offset = CommandContext.GetOptInt64(args, 2, 0);
      long limit = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var obj = ctx.ResolveTokenLive(module, token)
            ?? throw new CommandException(Wire.ErrNotFound, $"Token 0x{token:X8} does not resolve.");
        var cas = GetCustomAttributes(obj)
            ?? throw new CommandException(Wire.ErrBadArgs,
                $"Token 0x{token:X8} does not carry custom attributes.");
        var rows = new List<object>();
        foreach (var ca in cas) {
          var ctorArgs = ca.ConstructorArguments
              .Select(x => (object?)new object?[] { x.Type?.FullName, ValueToString(x.Value) })
              .ToArray();
          var namedArgs = ca.NamedArguments
              .Select(x => (object?)new object?[] {
                x.Name?.String, x.Type?.FullName, ValueToString(x.Value), x.IsField,
              })
              .ToArray();
          rows.Add(new object?[] { ca.TypeFullName, ctorArgs, namedArgs });
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object ResolveToken(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "token");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var obj = ctx.ResolveTokenLive(module, token);
        if (obj is null) {
          throw new CommandException(Wire.ErrNotFound, $"Token 0x{token:X8} does not resolve.");
        }
        string kind = obj switch {
          TypeDef => "type",
          MethodDef => "method",
          FieldDef => "field",
          PropertyDef => "property",
          EventDef => "event",
          ParamDef => "param",
          MemberRef => "memberref",
          TypeRef => "typeref",
          _ => obj.GetType().Name,
        };
        string fullName = (obj as IFullName)?.FullName ?? obj.ToString() ?? string.Empty;
        return new object?[] { (long)obj.MDToken.Raw, kind, fullName };
      });
    }

    private static object FindType(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string fullName = CommandContext.GetString(args, 1, "typeFullName");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var type = module.Find(fullName, isReflectionName: false)
            ?? throw new CommandException(Wire.ErrNotFound, $"Type not found: {fullName}");
        return CommandContext.TypeRow(type);
      });
    }

    private static object FindMethod(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string typeFullName = CommandContext.GetString(args, 1, "typeFullName");
      string methodName = CommandContext.GetString(args, 2, "methodName");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModule(file);
        var type = module.Find(typeFullName, isReflectionName: false)
            ?? throw new CommandException(Wire.ErrNotFound, $"Type not found: {typeFullName}");
        return type.Methods
            .Where(m => m.Name == methodName)
            .Select(CommandContext.MethodRow)
            .ToArray<object>();
      });
    }

    // ---- Helpers ------------------------------------------------------------------

    private static object? TokenOrNull(IMDTokenProvider? member) =>
        member is null ? null : (long)member.MDToken.Raw;

    private static CustomAttributeCollection? GetCustomAttributes(IMDTokenProvider obj) => obj switch {
      TypeDef t => t.CustomAttributes,
      MethodDef m => m.CustomAttributes,
      FieldDef f => f.CustomAttributes,
      PropertyDef p => p.CustomAttributes,
      EventDef e => e.CustomAttributes,
      ParamDef pd => pd.CustomAttributes,
      AssemblyDef a => a.CustomAttributes,
      ModuleDef md => md.CustomAttributes,
      _ => null,
    };

    /// <summary>Renders a metadata/attribute constant value to a display string.</summary>
    private static string? ValueToString(object? value) => value switch {
      null => null,
      UTF8String u => u.String,
      TypeSig ts => ts.FullName,
      _ => value.ToString(),
    };
  }
}
