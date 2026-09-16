using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>
  /// Assembly editing over dnlib: replace a method's IL, add/remove members, and a best-effort
  /// C# method-body edit via Roslyn (compile the declaring type + swap the body in). Edits mutate
  /// the live <see cref="ModuleDef"/>; persist with SAVE_MODULE and run with SAVE_MODULE→DBG_START.
  /// </summary>
  internal static class EditCommands {
    private static readonly Dictionary<string, OpCode> OpcodesByName = BuildOpcodeMap();

    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqReplaceMethodIl] = a => ReplaceMethodIl(ctx, a);
      t[Wire.ReqAddField] = a => AddField(ctx, a);
      t[Wire.ReqAddMethod] = a => AddMethod(ctx, a);
      t[Wire.ReqAddType] = a => AddType(ctx, a);
      t[Wire.ReqRemoveMember] = a => RemoveMember(ctx, a);
      t[Wire.ReqEditMethodCsharp] = a => EditMethodCsharp(ctx, a);
    }

    // ---- E1: REPLACE_METHOD_IL ------------------------------------------------------

    // Args: documentFilename, methodToken, instructions[], locals[]?, handlers[]?
    private static object ReplaceMethodIl(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      object[] instrs = GetArray(args, 2, "instructions");
      object[]? locals = args.Length > 3 ? args[3] as object[] : null;
      object[]? handlers = args.Length > 4 ? args[4] as object[] : null;
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var method = ctx.ResolveMember<MethodDef>(file, token, "method");
        method.Body = BuildCilBody(ctx, module, method, instrs, locals, handlers);
        RefreshDoc(ctx, file);
        return true;
      });
    }

    // ---- E2: add / remove members ---------------------------------------------------

    // Args: documentFilename, typeToken, name, fieldTypeToken, isStatic, attrs?
    private static object AddField(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint typeToken = CommandContext.GetToken(args, 1, "typeToken");
      string name = CommandContext.GetString(args, 2, "name");
      uint fieldTypeToken = CommandContext.GetToken(args, 3, "fieldTypeToken");
      bool isStatic = CommandContext.GetOptBool(args, 4, false);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var type = ctx.ResolveMember<TypeDef>(file, typeToken, "type");
        var attrs = FieldAttributes.Public | (isStatic ? FieldAttributes.Static : 0);
        var field = new FieldDefUser(name, new FieldSig(TokenToTypeSig(ctx, module, fieldTypeToken)), attrs);
        type.Fields.Add(field);
        uint token = ctx.RegisterNewMember(module, field);
        RefreshDoc(ctx, file);
        return new object?[] { (long)token, field.FullName };
      });
    }

    // Args: documentFilename, typeToken, name, returnTypeToken(0=void), paramTypeTokens[], isStatic,
    //       instructions[]?, locals[]?, handlers[]?  (if instructions omitted, a NotImplemented stub).
    private static object AddMethod(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint typeToken = CommandContext.GetToken(args, 1, "typeToken");
      string name = CommandContext.GetString(args, 2, "name");
      uint retToken = CommandContext.GetToken(args, 3, "returnTypeToken");
      object[] paramTokens = GetArray(args, 4, "paramTypeTokens");
      bool isStatic = CommandContext.GetOptBool(args, 5, false);
      object[]? instrs = args.Length > 6 ? args[6] as object[] : null;
      object[]? locals = args.Length > 7 ? args[7] as object[] : null;
      object[]? handlers = args.Length > 8 ? args[8] as object[] : null;
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var type = ctx.ResolveMember<TypeDef>(file, typeToken, "type");
        var retSig = TokenToTypeSig(ctx, module, retToken);
        var paramSigs = paramTokens.Select(p => TokenToTypeSig(ctx, module, Convert.ToUInt32(p))).ToArray();
        var sig = isStatic ? MethodSig.CreateStatic(retSig, paramSigs) : MethodSig.CreateInstance(retSig, paramSigs);
        var attrs = MethodAttributes.Public | MethodAttributes.HideBySig | (isStatic ? MethodAttributes.Static : 0);
        var method = new MethodDefUser(name, sig, MethodImplAttributes.IL | MethodImplAttributes.Managed, attrs);
        type.Methods.Add(method);
        method.Body = instrs is not null
            ? BuildCilBody(ctx, module, method, instrs, locals, handlers)
            : StubBody(module);
        uint token = ctx.RegisterNewMember(module, method);
        RefreshDoc(ctx, file);
        return new object?[] { (long)token, method.FullName };
      });
    }

    // Args: documentFilename, namespace, name, baseTypeToken?
    private static object AddType(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      string ns = CommandContext.GetOptString(args, 1) ?? string.Empty;
      string name = CommandContext.GetString(args, 2, "name");
      long baseTok = CommandContext.GetOptInt64(args, 3, 0);
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        ITypeDefOrRef baseType = baseTok != 0
            ? (ctx.ResolveTokenLive(module, unchecked((uint)baseTok)) as ITypeDefOrRef
               ?? throw new CommandException(Wire.ErrBadArgs, $"Base type token 0x{baseTok:X8} is not a type."))
            : module.CorLibTypes.Object.TypeDefOrRef;
        var type = new TypeDefUser(ns, name, baseType) {
          Attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
        };
        module.Types.Add(type);
        uint token = ctx.RegisterNewMember(module, type);
        RefreshDoc(ctx, file);
        return new object?[] { (long)token, type.FullName };
      });
    }

    // Args: documentFilename, memberToken
    private static object RemoveMember(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "memberToken");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var obj = ctx.ResolveTokenLive(module, token)
            ?? throw new CommandException(Wire.ErrNotFound, $"Token 0x{token:X8} does not resolve.");
        bool removed = obj switch {
          MethodDef m => m.DeclaringType?.Methods.Remove(m) ?? false,
          FieldDef f => f.DeclaringType?.Fields.Remove(f) ?? false,
          PropertyDef p => p.DeclaringType?.Properties.Remove(p) ?? false,
          EventDef e => e.DeclaringType?.Events.Remove(e) ?? false,
          TypeDef t => t.DeclaringType is not null ? t.DeclaringType.NestedTypes.Remove(t) : module.Types.Remove(t),
          _ => throw new CommandException(Wire.ErrBadArgs, $"Token 0x{token:X8} is not a removable member."),
        };
        if (removed) {
          ctx.ForgetSessionMember(module, token);
        }
        RefreshDoc(ctx, file);
        return removed;
      });
    }

    // ---- E3: EDIT_METHOD_CSHARP -----------------------------------------------------

    // Args: documentFilename, methodToken, csharpTypeSource (full declaring-type C#, one method edited).
    // Returns [true, warnings[]] on success, or [false, diagnostics[]] on compile failure
    // (diag = [severity, id, message, line]).
    private static object EditMethodCsharp(CommandContext ctx, object[] args) {
      string file = CommandContext.GetString(args, 0, "documentFilename");
      uint token = CommandContext.GetToken(args, 1, "methodToken");
      string source = CommandContext.GetString(args, 2, "csharpTypeSource");
      return ctx.RunOnUI<object>(() => {
        var module = ctx.ResolveModuleMD(file);
        var targetMethod = ctx.ResolveMember<MethodDef>(file, token, "method");
        var targetType = targetMethod.DeclaringType
            ?? throw new CommandException(Wire.ErrEdit, "Method has no declaring type.");

        var refs = BuildReferences(module, ctx);
        var compilation = CSharpCompilation.Create(
            "dnspy_automate_edit",
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success) {
          var diags = emit.Diagnostics
              .Where(d => d.Severity == DiagnosticSeverity.Error)
              .Select(d => (object)new object?[] {
                d.Severity.ToString(), d.Id, d.GetMessage(),
                d.Location.GetLineSpan().StartLinePosition.Line,
              }).ToArray();
          return new object?[] { false, diags };
        }

        var compiledModule = ModuleDefMD.Load(ms.ToArray());
        var compiledType = compiledModule.Find(targetType.FullName, isReflectionName: false)
            ?? throw new CommandException(Wire.ErrEdit, $"Compiled type not found: {targetType.FullName}");
        var compiledMethod = compiledType.Methods.FirstOrDefault(m =>
            m.Name == targetMethod.Name && new SigComparer().Equals(m.MethodSig, targetMethod.MethodSig));
        if (compiledMethod?.Body is null) {
          throw new CommandException(Wire.ErrEdit,
              $"Compiled method not found or has no body: {targetMethod.Name}");
        }

        var unmergeable = new List<string>();
        var body = ImportBody(compiledMethod, targetMethod, targetType, compiledType, module, unmergeable);
        // Airtight backstop for generic/anonymous-type helpers that reach the body via
        // TypeSpec/MethodSpec/MemberRef operands the per-Remap checks miss.
        CollectCompileRefs(body, compiledType.Module.Assembly, unmergeable);
        if (unmergeable.Count > 0) {
          throw new CommandException(Wire.ErrEdit,
              "Edit references members that can't be merged (compiler-generated helpers from lambdas/" +
              "local functions/async/iterators/anonymous types, or new members not in the target): " +
              string.Join(", ", unmergeable.Distinct()) +
              ". Rewrite the method without them, or edit IL directly.");
        }
        targetMethod.Body = body;
        RefreshDoc(ctx, file);
        return new object?[] { true, Array.Empty<object>() };
      });
    }

    // Builds the Roslyn reference set: the running runtime's trusted-platform assemblies (the full
    // BCL incl. System.Private.CoreLib, which DEFINES System.Object — a .NET Core module only names
    // the System.Runtime facade, so resolving its AssemblyRefs alone omits the corlib → CS0518/CS0012)
    // plus the sibling loaded user assemblies. Deduped by simple name (framework wins); the target's
    // own assembly is excluded since its type is supplied in source.
    private static List<MetadataReference> BuildReferences(ModuleDef module, CommandContext ctx) {
      var refs = new List<MetadataReference>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (module.Assembly?.Name is { } self) {
        seen.Add(self);
      }
      void TryAdd(string path, string simpleName) {
        if (!seen.Add(simpleName)) {
          return;
        }
        try {
          refs.Add(MetadataReference.CreateFromFile(path));
        } catch (Exception) {
          seen.Remove(simpleName);
        }
      }
      if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa) {
        foreach (var path in tpa.Split(Path.PathSeparator)) {
          if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) {
            TryAdd(path, Path.GetFileNameWithoutExtension(path));
          }
        }
      }
      foreach (var doc in ctx.DocumentService.GetDocuments()) {
        if (doc.AssemblyDef?.Name is { } n && !string.IsNullOrEmpty(doc.Filename) && File.Exists(doc.Filename)) {
          TryAdd(doc.Filename!, n);
        }
      }
      return refs;
    }

    /// <summary>
    /// Imports a compiled method's body into the target module, mapping references to the compiled
    /// declaring type back onto the real target type (by name+signature) and importing everything
    /// else via dnlib's <see cref="Importer"/>. Unmergeable compiler-generated types add a warning.
    /// </summary>
    private static CilBody ImportBody(MethodDef compiled, MethodDef target, TypeDef targetType,
        TypeDef compiledType, ModuleDef module, List<string> unmergeable) {
      var old = compiled.Body;
      var importer = new Importer(module);
      var body = new CilBody { InitLocals = old.InitLocals };

      var localMap = new Dictionary<Local, Local>();
      foreach (var ol in old.Variables) {
        TypeSig sig;
        if (ol.Type.ToTypeDefOrRef() is TypeDef ltd && ltd.Module == compiledType.Module) {
          if (ltd.FullName == compiledType.FullName) {
            sig = targetType.ToTypeSig();
          } else {
            unmergeable.Add("local type " + ltd.FullName);
            sig = importer.Import(ol.Type);
          }
        } else {
          sig = importer.Import(ol.Type);
        }
        var nl = new Local(sig, ol.Name);
        body.Variables.Add(nl);
        localMap[ol] = nl;
      }

      var instrMap = new Dictionary<Instruction, Instruction>();
      foreach (var oi in old.Instructions) {
        var ni = Instruction.Create(OpCodes.Nop);
        ni.OpCode = oi.OpCode;
        instrMap[oi] = ni;
        body.Instructions.Add(ni);
      }

      object? Remap(object? operand) => operand switch {
        null => null,
        Instruction ins => instrMap.TryGetValue(ins, out var mi) ? mi : ins,
        IList<Instruction> list => list.Select(x => instrMap.TryGetValue(x, out var mi) ? mi : x).ToList(),
        string s => s,
        Local l => localMap.TryGetValue(l, out var ml) ? ml : l,
        Parameter p => target.Parameters[p.Index],
        ITypeDefOrRef tr => RemapType(tr, targetType, compiledType, module, importer, unmergeable),
        MethodDef md => RemapMethod(md, targetType, compiledType, importer, unmergeable),
        FieldDef fd => RemapField(fd, targetType, compiledType, importer, unmergeable),
        MemberRef mr => mr.IsFieldRef ? importer.Import(mr) : (object)importer.Import(mr),
        MethodSpec ms => importer.Import(ms),
        _ => operand,
      };

      foreach (var oi in old.Instructions) {
        instrMap[oi].Operand = Remap(oi.Operand);
      }

      foreach (var eh in old.ExceptionHandlers) {
        body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType) {
          TryStart = MapInstr(eh.TryStart, instrMap),
          TryEnd = MapInstr(eh.TryEnd, instrMap),
          HandlerStart = MapInstr(eh.HandlerStart, instrMap),
          HandlerEnd = MapInstr(eh.HandlerEnd, instrMap),
          FilterStart = MapInstr(eh.FilterStart, instrMap),
          CatchType = eh.CatchType is null ? null : RemapType(eh.CatchType, targetType, compiledType, module, importer, unmergeable),
        });
      }

      body.UpdateInstructionOffsets();
      return body;
    }

    private static Instruction? MapInstr(Instruction? i, Dictionary<Instruction, Instruction> map) =>
        i is not null && map.TryGetValue(i, out var m) ? m : null;

    private static ITypeDefOrRef RemapType(ITypeDefOrRef t, TypeDef targetType, TypeDef compiledType,
        ModuleDef module, Importer importer, List<string> unmergeable) {
      if (t is TypeDef td && td.Module == compiledType.Module) {
        if (td.FullName == compiledType.FullName) {
          return targetType;
        }
        unmergeable.Add("type " + td.FullName);
      }
      return importer.Import(t);
    }

    // MethodDef/FieldDef operands are always defined in the compiled module (external members are
    // MemberRefs). Own-declaring-type members map to the real target member; anything else — a
    // compiler-generated helper, or a member absent from the target — is unmergeable.
    private static IMethod RemapMethod(MethodDef md, TypeDef targetType, TypeDef compiledType,
        Importer importer, List<string> unmergeable) {
      if (md.DeclaringType?.FullName == compiledType.FullName) {
        var match = targetType.Methods.FirstOrDefault(x =>
            x.Name == md.Name && new SigComparer().Equals(x.MethodSig, md.MethodSig));
        if (match is not null) {
          return match;
        }
      }
      unmergeable.Add("method " + md.FullName);
      return importer.Import(md);
    }

    private static IField RemapField(FieldDef fd, TypeDef targetType, TypeDef compiledType,
        Importer importer, List<string> unmergeable) {
      if (fd.DeclaringType?.FullName == compiledType.FullName) {
        var match = targetType.Fields.FirstOrDefault(x =>
            x.Name == fd.Name && new SigComparer().Equals(x.FieldSig, fd.FieldSig));
        if (match is not null) {
          return match;
        }
      }
      unmergeable.Add("field " + fd.FullName);
      return importer.Import(fd);
    }

    // Recursively flags every reference in the built body that is still rooted in the throwaway
    // compile assembly. Own-type refs were already remapped to the target module by ImportBody, so
    // only truly dangling refs (anonymous types, lambda/local-fn/async/iterator helpers, same-type
    // members absent from the target) carry the compile-assembly identity — including generic ones
    // that arrive as TypeSpec/MethodSpec/MemberRef operands or generic local types.
    private static void CollectCompileRefs(CilBody body, IAssembly? compileAsm, List<string> unmergeable) {
      if (compileAsm is null) {
        return;
      }
      var seen = new HashSet<string>(StringComparer.Ordinal);

      bool IsCompile(IAssembly? a) => a is not null && UTF8String.Equals(a.Name, compileAsm.Name);

      void Flag(ITypeDefOrRef t) {
        if (seen.Add(t.FullName)) {
          unmergeable.Add("type " + t.FullName);
        }
      }

      void CheckTypeDefOrRef(ITypeDefOrRef? t) {
        if (t is null) {
          return;
        }
        if (IsCompile(t.DefinitionAssembly)) {
          Flag(t);
        }
        if (t is TypeSpec ts) {
          CheckSig(ts.TypeSig);
        }
      }

      void CheckSig(TypeSig? sig) {
        while (sig is not null) {
          if (sig is GenericInstSig gis) {
            CheckTypeDefOrRef(gis.GenericType?.TypeDefOrRef);
            foreach (var arg in gis.GenericArguments) {
              CheckSig(arg);
            }
            return;
          }
          if (sig is TypeDefOrRefSig tdr) {
            CheckTypeDefOrRef(tdr.TypeDefOrRef);
            return;
          }
          sig = sig.Next;
        }
      }

      void CheckMethod(IMethod? m) {
        if (m is null) {
          return;
        }
        CheckTypeDefOrRef(m.DeclaringType);
        if (m is MethodSpec ms && ms.GenericInstMethodSig is { } gim) {
          foreach (var arg in gim.GenericArguments) {
            CheckSig(arg);
          }
        }
      }

      foreach (var instr in body.Instructions) {
        switch (instr.Operand) {
          case ITypeDefOrRef t:
            CheckTypeDefOrRef(t);
            break;
          case MethodSpec ms:
            CheckMethod(ms);
            break;
          case MemberRef mr:
            CheckTypeDefOrRef(mr.DeclaringType);
            break;
          case IMethod m:
            CheckTypeDefOrRef(m.DeclaringType);
            break;
          case IField f:
            CheckTypeDefOrRef(f.DeclaringType);
            break;
        }
      }
      foreach (var local in body.Variables) {
        CheckSig(local.Type);
      }
      foreach (var eh in body.ExceptionHandlers) {
        CheckTypeDefOrRef(eh.CatchType);
      }
    }

    // ---- Shared IL-body builder + helpers -------------------------------------------

    private static CilBody BuildCilBody(CommandContext ctx, ModuleDefMD module, MethodDef method,
        object[] instrRows, object[]? localRows, object[]? handlerRows) {
      if (instrRows.Length == 0) {
        throw new CommandException(Wire.ErrBadArgs, "instructions must not be empty.");
      }
      var body = new CilBody();

      if (localRows is not null) {
        foreach (var row in localRows) {
          body.Variables.Add(new Local(TokenToTypeSig(ctx, module, Convert.ToUInt32(row))));
        }
      } else if (method.Body is CilBody oldBody) {
        // Preserve the method's existing locals when the caller omits them, so instruction operands
        // that reference a local index still resolve (the natural "edit one instruction" round-trip).
        foreach (var v in oldBody.Variables) {
          body.Variables.Add(new Local(v.Type, v.Name));
        }
      }

      var instrs = new List<Instruction>();
      var branchFix = new List<(int Index, int Target)>();
      var switchFix = new List<(int Index, int[] Targets)>();
      for (int i = 0; i < instrRows.Length; i++) {
        if (instrRows[i] is not object[] row || row.Length == 0 || row[0] is not string name) {
          throw new CommandException(Wire.ErrBadArgs, $"Instruction {i} must be [opcodeName, operand].");
        }
        if (!OpcodesByName.TryGetValue(name, out var op)) {
          throw new CommandException(Wire.ErrBadArgs, $"Unknown opcode: {name}");
        }
        object? operand = row.Length > 1 ? row[1] : null;
        Instruction instr;
        switch (op.OperandType) {
          case OperandType.InlineNone:
            instr = Instruction.Create(op);
            break;
          case OperandType.InlineBrTarget:
          case OperandType.ShortInlineBrTarget:
            // Instruction.Create(op) validates a no-operand opcode; assign the branch opcode
            // directly (operand is filled by the branchFix second pass).
            instr = Instruction.Create(OpCodes.Nop);
            instr.OpCode = op;
            branchFix.Add((i, Convert.ToInt32(operand)));
            break;
          case OperandType.InlineSwitch:
            instr = Instruction.Create(OpCodes.Nop);
            instr.OpCode = op;
            switchFix.Add((i, GetArrayValue(operand, name).Select(Convert.ToInt32).ToArray()));
            break;
          case OperandType.InlineI:
            instr = Instruction.Create(op, Convert.ToInt32(operand));
            break;
          case OperandType.ShortInlineI:
            instr = op.Code == Code.Ldc_I4_S
                ? Instruction.Create(op, Convert.ToSByte(operand))
                : Instruction.Create(op, Convert.ToByte(operand));
            break;
          case OperandType.InlineI8:
            instr = Instruction.Create(op, Convert.ToInt64(operand));
            break;
          case OperandType.ShortInlineR:
            instr = Instruction.Create(op, Convert.ToSingle(operand));
            break;
          case OperandType.InlineR:
            instr = Instruction.Create(op, Convert.ToDouble(operand));
            break;
          case OperandType.InlineString:
            instr = Instruction.Create(op, Convert.ToString(operand) ?? string.Empty);
            break;
          case OperandType.InlineVar:
          case OperandType.ShortInlineVar:
            int vi = Convert.ToInt32(operand);
            instr = IsArgOpcode(op)
                ? Instruction.Create(op, method.Parameters[vi])
                : Instruction.Create(op, body.Variables[vi]);
            break;
          case OperandType.InlineMethod:
            instr = Instruction.Create(op, ResolveTok<IMethod>(ctx, module, operand, "method"));
            break;
          case OperandType.InlineField:
            instr = Instruction.Create(op, ResolveTok<IField>(ctx, module, operand, "field"));
            break;
          case OperandType.InlineType:
            instr = Instruction.Create(op, ResolveTok<ITypeDefOrRef>(ctx, module, operand, "type"));
            break;
          case OperandType.InlineTok:
            instr = CreateLdtoken(ctx, op, module, operand);
            break;
          default:
            throw new CommandException(Wire.ErrBadArgs,
                $"Unsupported operand type {op.OperandType} for opcode {name}.");
        }
        instrs.Add(instr);
      }

      if (instrs[instrs.Count - 1].OpCode.FlowControl is not
          (FlowControl.Return or FlowControl.Throw or FlowControl.Branch)) {
        throw new CommandException(Wire.ErrBadArgs,
            "method body must end with a terminating instruction (ret/throw/br/leave).");
      }

      foreach (var (index, targetIndex) in branchFix) {
        instrs[index].Operand = instrs[CheckIndex(targetIndex, instrs.Count, "branch target")];
      }
      foreach (var (index, targets) in switchFix) {
        instrs[index].Operand = targets.Select(x => instrs[CheckIndex(x, instrs.Count, "switch target")]).ToList();
      }
      foreach (var instr in instrs) {
        body.Instructions.Add(instr);
      }

      if (handlerRows is not null) {
        foreach (var row in handlerRows) {
          if (row is not object[] h || h.Length < 5 || h[0] is not string ht) {
            throw new CommandException(Wire.ErrBadArgs,
                "Handler must be [type, tryStart, tryEnd, handlerStart, handlerEnd, catchType?].");
          }
          body.ExceptionHandlers.Add(new ExceptionHandler(ParseHandlerType(ht)) {
            TryStart = instrs[CheckIndex(Convert.ToInt32(h[1]), instrs.Count, "tryStart")],
            TryEnd = IndexOrNull(instrs, Convert.ToInt32(h[2])),
            HandlerStart = instrs[CheckIndex(Convert.ToInt32(h[3]), instrs.Count, "handlerStart")],
            HandlerEnd = IndexOrNull(instrs, Convert.ToInt32(h[4])),
            CatchType = h.Length > 5 && h[5] is not null
                ? ResolveTok<ITypeDefOrRef>(ctx, module, h[5], "catchType") : null,
          });
        }
      }

      body.SimplifyBranches();
      body.OptimizeBranches();
      body.UpdateInstructionOffsets();
      return body;
    }

    private static CilBody StubBody(ModuleDef module) {
      var ctor = new Importer(module).Import(
          typeof(System.NotImplementedException).GetConstructor(Type.EmptyTypes)!);
      var body = new CilBody();
      body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor));
      body.Instructions.Add(Instruction.Create(OpCodes.Throw));
      body.UpdateInstructionOffsets();
      return body;
    }

    private static Instruction CreateLdtoken(CommandContext ctx, OpCode op, ModuleDefMD module, object? operand) {
      var obj = ctx.ResolveTokenLive(module, Convert.ToUInt32(operand))
          ?? throw new CommandException(Wire.ErrNotFound, "ldtoken operand does not resolve.");
      return obj switch {
        ITypeDefOrRef t => Instruction.Create(op, t),
        IMethod m => Instruction.Create(op, m),
        IField f => Instruction.Create(op, f),
        _ => throw new CommandException(Wire.ErrBadArgs, "ldtoken operand is not a type/method/field."),
      };
    }

    private static T ResolveTok<T>(CommandContext ctx, ModuleDefMD module, object? operand, string kind) where T : class {
      var obj = ctx.ResolveTokenLive(module, Convert.ToUInt32(operand))
          ?? throw new CommandException(Wire.ErrNotFound, $"{kind} token does not resolve.");
      return obj as T ?? throw new CommandException(Wire.ErrBadArgs, $"Token is not a {kind}.");
    }

    private static TypeSig TokenToTypeSig(CommandContext ctx, ModuleDefMD module, uint token) {
      if (token == 0) {
        return module.CorLibTypes.Void;
      }
      var t = ctx.ResolveTokenLive(module, token) as ITypeDefOrRef
          ?? throw new CommandException(Wire.ErrBadArgs, $"Token 0x{token:X8} is not a type.");
      return t.ToTypeSig();
    }

    private static bool IsArgOpcode(OpCode op) =>
        op.Name is { } n && (n.StartsWith("ldarg", StringComparison.Ordinal) || n.StartsWith("starg", StringComparison.Ordinal));

    private static ExceptionHandlerType ParseHandlerType(string s) => s.ToLowerInvariant() switch {
      "catch" => ExceptionHandlerType.Catch,
      "finally" => ExceptionHandlerType.Finally,
      "filter" => ExceptionHandlerType.Filter,
      "fault" => ExceptionHandlerType.Fault,
      _ => throw new CommandException(Wire.ErrBadArgs, $"Unknown handler type: {s}"),
    };

    private static int CheckIndex(int index, int count, string what) =>
        index >= 0 && index < count ? index
            : throw new CommandException(Wire.ErrBadArgs, $"{what} index {index} out of range (0..{count - 1}).");

    private static Instruction? IndexOrNull(List<Instruction> instrs, int index) =>
        index >= 0 && index < instrs.Count ? instrs[index] : null;

    private static object[] GetArray(object[] args, int index, string name) =>
        index < args.Length && args[index] is object[] arr
            ? arr
            : throw new CommandException(Wire.ErrBadArgs, $"Missing or non-array argument '{name}'.");

    private static object[] GetArrayValue(object? value, string name) =>
        value as object[] ?? throw new CommandException(Wire.ErrBadArgs, $"Operand for '{name}' must be an array.");

    private static void RefreshDoc(CommandContext ctx, string file) {
      var doc = ctx.FindDocument(file);
      if (doc is not null) {
        ctx.TabService.RefreshModifiedDocument(doc);
      }
    }

    private static Dictionary<string, OpCode> BuildOpcodeMap() {
      var map = new Dictionary<string, OpCode>(StringComparer.Ordinal);
      var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
      foreach (var f in typeof(OpCodes).GetFields(flags)) {
        if (f.FieldType == typeof(OpCode) && f.GetValue(null) is OpCode oc && oc.Name is { } n) {
          map[n] = oc;
        }
      }
      return map;
    }
  }
}
