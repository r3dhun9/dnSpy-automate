using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Reads a method's CIL body (instructions, locals, exception handlers) via dnlib in a form that
  /// round-trips through REPLACE_METHOD_IL: each instruction carries a REPLACE-compatible structured
  /// operand alongside its display string; locals carry a type token; handlers use instruction
  /// indices + a catch token.
  /// </summary>
  internal static class IlCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqGetMethodIl] = a => GetMethodIl(ctx, a);
    }

    private static object GetMethodIl(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      return ctx.RunOnUI<object>(() => {
        var method = ctx.ResolveMember<MethodDef>(file, token, "method");
        if (!method.HasBody || method.Body is not CilBody body) {
          throw new CommandException(Wire.ErrNotFound, $"Method 0x{token:X8} has no IL body.");
        }
        body.UpdateInstructionOffsets();

        var indexMap = new Dictionary<Instruction, int>();
        for (int i = 0; i < body.Instructions.Count; i++) {
          indexMap[body.Instructions[i]] = i;
        }
        int count = body.Instructions.Count;

        // Instruction: [offset, opcodeName, operandDisplay, structuredOperand]. The 4th field is the
        // REPLACE_METHOD_IL operand; take [opcodeName, structuredOperand] (fields 1,3) to write back.
        var instructions = body.Instructions
            .Select(instr => (object)new object?[] {
              (long)instr.Offset,
              instr.OpCode.Name,
              OperandToString(instr.Operand),
              StructuredOperand(instr.Operand, indexMap),
            }).ToArray();

        // Local: [index, name, typeFullName, typeToken]. typeToken feeds REPLACE locals[]; null for
        // array/generic sigs with no metadata token (omit locals in REPLACE to keep them instead).
        var locals = body.Variables
            .Select(v => (object)new object?[] {
              v.Index,
              v.Name,
              v.Type?.FullName,
              TypeToken(v.Type),
            }).ToArray();

        // Handler: [type, tryStartIdx, tryEndIdx, handlerStartIdx, handlerEndIdx, catchTypeToken] —
        // the exact shape REPLACE handlers[] accepts (end boundaries are index==count for end-of-method).
        var handlers = body.ExceptionHandlers
            .Select(h => (object)new object?[] {
              h.HandlerType.ToString(),
              HandlerIndex(h.TryStart, indexMap, count),
              HandlerIndex(h.TryEnd, indexMap, count),
              HandlerIndex(h.HandlerStart, indexMap, count),
              HandlerIndex(h.HandlerEnd, indexMap, count),
              h.CatchType is not null ? (long)h.CatchType.MDToken.Raw : (object?)null,
            }).ToArray();

        return new object[] { instructions, locals, handlers };
      });
    }

    private static object? StructuredOperand(object? operand, Dictionary<Instruction, int> indexMap) {
      switch (operand) {
        case null:
          return null;
        case Instruction target:
          return indexMap.TryGetValue(target, out var ti) ? (long)ti : (object?)null;
        case IList<Instruction> targets:
          return targets.Select(t => (object?)(indexMap.TryGetValue(t, out var i) ? (long)i : (object?)null)).ToArray();
        case string s:
          return s;
        case Local local:
          return (long)local.Index;
        case Parameter param:
          return (long)param.Index;
        case IMDTokenProvider member:  // method / field / type / tok operand
          return (long)member.MDToken.Raw;
        case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
          return operand;
        default:
          return null;
      }
    }

    private static object? TypeToken(TypeSig? type) {
      var t = type?.ToTypeDefOrRef();
      return t is not null && t.MDToken.Rid != 0 ? (long)t.MDToken.Raw : (object?)null;
    }

    private static object? HandlerIndex(Instruction? instr, Dictionary<Instruction, int> indexMap, int count) {
      if (instr is null) {
        return (long)count;  // boundary at end of method; REPLACE reads index==count as "to end".
      }
      return indexMap.TryGetValue(instr, out var i) ? (long)i : (long)count;
    }

    /// <summary>Renders an instruction operand to a display string (readability alongside the structured form).</summary>
    private static string? OperandToString(object? operand) {
      switch (operand) {
        case null:
          return null;
        case Instruction target:
          return $"IL_{target.Offset:X4}";
        case Instruction[] targets:
          return string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}"));
        case string s:
          return "\"" + s + "\"";
        case IFullName named:
          return named.FullName;
        case Local local:
          return $"V_{local.Index}";
        case Parameter param:
          return param.IsNormalMethodParameter ? (param.Name ?? $"arg{param.Index}") : $"arg{param.Index}";
        default:
          return operand.ToString();
      }
    }
  }
}
