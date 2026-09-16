using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Name/literal/number search reimplemented over dnlib (dnSpy's IDocumentSearcherProvider is
  /// internal and not referenceable). Rows: [kind, name, context, documentFilename, token].
  /// Response envelope: [rows, total] — rows is the offset/limit window, total is the full match count.
  /// </summary>
  internal static class SearchCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqSearch] = a => Search(ctx, a);
    }

    // Args: query, caseSensitive?, wholeWords?, mode?("name"|"string"|"number"), docFilename?,
    //       offset?, limit?, regex?, kinds?(csv).
    private static object Search(CommandContext ctx, object[] args) {
      string query = CommandContext.GetString(args, 0, "query");
      bool caseSensitive = CommandContext.GetOptBool(args, 1, false);
      bool wholeWords = CommandContext.GetOptBool(args, 2, false);
      string mode = (CommandContext.GetOptString(args, 3) ?? "name").ToLowerInvariant();
      string? docFilename = CommandContext.GetOptString(args, 4);
      long offset = CommandContext.GetOptInt64(args, 5, 0);
      long limit = CommandContext.GetOptInt64(args, 6, 0);
      bool useRegex = CommandContext.GetOptBool(args, 7, false);
      var kinds = ParseKinds(CommandContext.GetOptString(args, 8));

      var matches = BuildMatcher(query, caseSensitive, wholeWords, useRegex);
      double? number = mode == "number" &&
          double.TryParse(query, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
              ? n : (double?)null;

      return ctx.RunOnUI<object>(() => {
        var modules = string.IsNullOrEmpty(docFilename)
            ? AllModules(ctx)
            : new[] { ctx.ResolveModule(docFilename) };

        var rows = new List<object>();
        foreach (var module in modules) {
          string file = module.Location ?? module.Name?.String ?? string.Empty;
          switch (mode) {
            case "string":
              SearchLiterals(module, file, matches, rows);
              break;
            case "number":
              SearchNumbers(module, file, number, rows);
              break;
            default:
              SearchNames(module, file, matches, kinds, rows);
              break;
          }
        }
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static void SearchNames(ModuleDef module, string file, Func<string?, bool> matches,
        HashSet<string>? kinds, List<object> rows) {
      bool Want(string k) => kinds is null || kinds.Contains(k);
      var namespaces = new HashSet<string>(StringComparer.Ordinal);
      foreach (var type in module.GetTypes()) {
        string ns = type.Namespace?.String ?? string.Empty;
        if (Want("namespace") && ns.Length != 0 && namespaces.Add(ns) && matches(ns)) {
          rows.Add(new object?[] { "namespace", ns, ns, file, null });
        }
        if (Want("type") && matches(type.Name)) {
          rows.Add(new object?[] { "type", type.Name?.String, type.FullName, file, (long)type.MDToken.Raw });
        }
        if (Want("method")) {
          foreach (var m in type.Methods) {
            if (matches(m.Name)) {
              rows.Add(Row("method", m.Name, type, file, m.MDToken));
            }
          }
        }
        if (Want("field")) {
          foreach (var f in type.Fields) {
            if (matches(f.Name)) {
              rows.Add(Row("field", f.Name, type, file, f.MDToken));
            }
          }
        }
        if (Want("property")) {
          foreach (var p in type.Properties) {
            if (matches(p.Name)) {
              rows.Add(Row("property", p.Name, type, file, p.MDToken));
            }
          }
        }
        if (Want("event")) {
          foreach (var e in type.Events) {
            if (matches(e.Name)) {
              rows.Add(Row("event", e.Name, type, file, e.MDToken));
            }
          }
        }
      }
    }

    private static void SearchLiterals(
        ModuleDef module, string file, Func<string?, bool> matches, List<object> rows) {
      foreach (var type in module.GetTypes()) {
        foreach (var f in type.Fields) {
          if (f.Constant?.Value is string cs && matches(cs)) {
            rows.Add(new object?[] { "const", cs, type.FullName, file, (long)f.MDToken.Raw });
          }
        }
        foreach (var m in type.Methods) {
          if (m.Body is not CilBody body) {
            continue;
          }
          foreach (var instr in body.Instructions) {
            if (instr.OpCode.Code == Code.Ldstr && instr.Operand is string s && matches(s)) {
              rows.Add(new object?[] {
                "string", s, $"{type.FullName}::{m.Name}", file, (long)m.MDToken.Raw,
              });
            }
          }
        }
      }
    }

    private static void SearchNumbers(ModuleDef module, string file, double? number, List<object> rows) {
      if (number is null) {
        return;
      }
      double target = number.Value;
      foreach (var type in module.GetTypes()) {
        foreach (var f in type.Fields) {
          if (f.Constant?.Value is { } cv && IsNumeric(cv, out double fd) && fd == target) {
            rows.Add(new object?[] {
              "const", Convert.ToString(cv, CultureInfo.InvariantCulture), type.FullName, file,
              (long)f.MDToken.Raw,
            });
          }
        }
        foreach (var m in type.Methods) {
          if (m.Body is not CilBody body) {
            continue;
          }
          foreach (var instr in body.Instructions) {
            if (LdcValue(instr) is double v && v == target) {
              rows.Add(new object?[] {
                "number", v.ToString(CultureInfo.InvariantCulture), $"{type.FullName}::{m.Name}",
                file, (long)m.MDToken.Raw,
              });
            }
          }
        }
      }
    }

    private static Func<string?, bool> BuildMatcher(
        string query, bool caseSensitive, bool wholeWords, bool useRegex) {
      if (useRegex) {
        Regex re;
        try {
          re = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        } catch (ArgumentException ex) {
          throw new CommandException(Wire.ErrBadArgs, $"Invalid regex: {ex.Message}");
        }
        return c => c is not null && re.IsMatch(c);
      }
      var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
      return c => c is not null &&
          (wholeWords ? string.Equals(c, query, cmp) : c.IndexOf(query, cmp) >= 0);
    }

    private static HashSet<string>? ParseKinds(string? kinds) {
      if (string.IsNullOrWhiteSpace(kinds)) {
        return null;
      }
      return new HashSet<string>(
          kinds!.Split(',').Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length != 0),
          StringComparer.Ordinal);
    }

    private static bool IsNumeric(object v, out double d) {
      switch (v) {
        case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
          d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
          return true;
        default:
          d = 0;
          return false;
      }
    }

    private static double? LdcValue(Instruction instr) {
      if (instr.IsLdcI4()) {
        return instr.GetLdcI4Value();
      }
      return instr.Operand switch {
        long l => l,
        float f => f,
        double db => db,
        _ => (double?)null,
      };
    }

    private static object?[] Row(string kind, UTF8String? name, TypeDef declaringType, string file, MDToken token) =>
        new object?[] { kind, name?.String, declaringType.FullName, file, (long)token.Raw };

    private static IEnumerable<ModuleDef> AllModules(CommandContext ctx) {
      foreach (var doc in ctx.DocumentService.GetDocuments()) {
        if (doc.ModuleDef is not null) {
          yield return doc.ModuleDef;
        }
        foreach (var child in doc.Children) {
          if (child.ModuleDef is not null) {
            yield return child.ModuleDef;
          }
        }
      }
    }
  }
}
