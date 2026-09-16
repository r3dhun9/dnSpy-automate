using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Settings;
using dnlib.DotNet;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Shared state and helpers passed to every command module: the imported dnSpy services,
  /// the UI-thread marshaler, argument parsing, document/member resolution, and the common
  /// positional row builders. Handler modules call these from inside <see cref="RunOnUI{T}"/>
  /// whenever they touch dnSpy or dnlib state.
  /// </summary>
  internal sealed class CommandContext {
    private readonly UiThread _ui;

    /// <summary>Creates the context around the imported services.</summary>
    public CommandContext(
        UiThread ui,
        IAppWindow appWindow,
        IDsDocumentService documentService,
        IDecompilerService decompilerService,
        IDocumentTabService tabService,
        IOutputService outputService,
        ISettingsService settingsService,
        IBamlDecompiler? bamlDecompiler,
        DebuggerServices? debugger) {
      _ui = ui;
      AppWindow = appWindow;
      DocumentService = documentService;
      DecompilerService = decompilerService;
      TabService = tabService;
      OutputService = outputService;
      SettingsService = settingsService;
      BamlDecompiler = bamlDecompiler;
      Debugger = debugger;
    }

    public IAppWindow AppWindow { get; }
    public IDsDocumentService DocumentService { get; }
    public IDecompilerService DecompilerService { get; }
    public IDocumentTabService TabService { get; }
    public IOutputService OutputService { get; }
    public ISettingsService SettingsService { get; }

    /// <summary>The BAML→XAML decompiler, or null if the BAML extension isn't loaded.</summary>
    public IBamlDecompiler? BamlDecompiler { get; }

    /// <summary>Debugger services, or null if the debugger extension isn't loaded.</summary>
    public DebuggerServices? Debugger { get; }

    /// <summary>Returns the debugger services or throws XERROR_UNAVAILABLE.</summary>
    public DebuggerServices RequireDebugger() =>
        Debugger ?? throw new CommandException(Wire.ErrUnavailable, "Debugger is not available.");

    /// <summary>Runs a delegate on the WPF UI thread (see <see cref="UiThread"/>).</summary>
    public T RunOnUI<T>(Func<T> func) => _ui.RunOnUI(func);

    // ---- Document / member resolution (call inside RunOnUI) -----------------------

    /// <summary>Finds a loaded document by its full filename (case-insensitive).</summary>
    public IDsDocument? FindDocument(string filename) {
      foreach (var doc in DocumentService.GetDocuments()) {
        if (string.Equals(doc.Filename, filename, StringComparison.OrdinalIgnoreCase)) {
          return doc;
        }
      }
      return null;
    }

    /// <summary>Finds a document by filename and returns its dnlib module, or throws.</summary>
    public ModuleDef ResolveModule(string filename) {
      var doc = FindDocument(filename)
          ?? throw new CommandException(Wire.ErrNotFound, $"Document not loaded: {filename}");
      return doc.ModuleDef
          ?? throw new CommandException(Wire.ErrNotFound, $"Not a .NET module: {filename}");
    }

    /// <summary>Like <see cref="ResolveModule"/> but requires a metadata-backed module.</summary>
    public ModuleDefMD ResolveModuleMD(string filename) {
      if (ResolveModule(filename) is not ModuleDefMD moduleMD) {
        throw new CommandException(Wire.ErrNotFound, $"Module has no metadata tokens: {filename}");
      }
      return moduleMD;
    }

    /// <summary>Resolves a metadata token to a member of the expected type, or throws.</summary>
    /// <typeparam name="T">Expected member type (e.g. <c>TypeDef</c>, <c>MethodDef</c>).</typeparam>
    public T ResolveMember<T>(string filename, uint token, string kind) where T : class {
      var module = ResolveModuleMD(filename);
      object? obj = module.ResolveToken(token);
      obj ??= TryGetSessionMember(module, token);
      if (obj is null) {
        throw new CommandException(Wire.ErrNotFound, $"Token 0x{token:X8} does not resolve.");
      }
      if (obj is not T typed) {
        throw new CommandException(Wire.ErrBadArgs, $"Token 0x{token:X8} is not a {kind}.");
      }
      return typed;
    }

    // ---- New-member session tokens --------------------------------------------------
    // dnlib leaves a freshly-created member at rid 0 until the module is written, so it isn't
    // resolvable by token. RegisterNewMember gives it a unique synthetic rid (above any real table
    // row, so ResolveToken returns null) and records it here; ResolveTokenLive / ResolveMember fall
    // back to this map. The table self-evicts when a document's ModuleDef is replaced (unload/reload).

    private sealed class ModuleEditState {
      public readonly Dictionary<uint, IMemberDef> Members = new Dictionary<uint, IMemberDef>();
      public uint NextRid = 0x00E00000;
    }

    private readonly ConditionalWeakTable<ModuleDef, ModuleEditState> _editState =
        new ConditionalWeakTable<ModuleDef, ModuleEditState>();

    /// <summary>Assigns a synthetic rid to a new member and records it; returns its packed token.</summary>
    public uint RegisterNewMember(ModuleDef module, IMemberDef member) {
      var state = _editState.GetOrCreateValue(module);
      member.Rid = state.NextRid++;
      uint token = member.MDToken.Raw;
      state.Members[token] = member;
      return token;
    }

    /// <summary>Resolves a token against the module metadata, then the new-member session map.</summary>
    public IMDTokenProvider? ResolveTokenLive(ModuleDefMD module, uint token) {
      IMDTokenProvider? obj = module.ResolveToken(token);
      return obj ?? TryGetSessionMember(module, token);
    }

    /// <summary>Drops a member from the new-member session map (e.g. after removal).</summary>
    public void ForgetSessionMember(ModuleDef module, uint token) {
      if (_editState.TryGetValue(module, out var state)) {
        state.Members.Remove(token);
      }
    }

    private IMemberDef? TryGetSessionMember(ModuleDef module, uint token) =>
        _editState.TryGetValue(module, out var state) && state.Members.TryGetValue(token, out var m) ? m : null;

    /// <summary>Resolves a decompiler by unique/generic guid, or the current default if null.</summary>
    public IDecompiler ResolveDecompiler(string? langGuid) {
      if (string.IsNullOrEmpty(langGuid)) {
        return DecompilerService.Decompiler;
      }
      if (!Guid.TryParse(langGuid, out var guid)) {
        throw new CommandException(Wire.ErrBadArgs, $"Invalid decompiler guid: {langGuid}");
      }
      var found = DecompilerService.Find(guid);
      if (found is not null) {
        return found;
      }
      foreach (var d in DecompilerService.AllDecompilers) {
        if (d.UniqueGuid == guid || d.GenericGuid == guid) {
          return d;
        }
      }
      throw new CommandException(Wire.ErrNotFound, $"Unknown decompiler guid: {langGuid}");
    }

    // ---- Row builders (positional; element order is the wire contract) -------------

    /// <summary>Type row: [token, fullName, name, namespace, attributes].</summary>
    public static object[] TypeRow(TypeDef t) => new object[] {
      (long)t.MDToken.Raw,
      t.FullName,
      t.Name?.String ?? string.Empty,
      t.Namespace?.String ?? string.Empty,
      (long)t.Attributes,
    };

    /// <summary>Method row: [token, name, fullName, isStatic, attributes].</summary>
    public static object[] MethodRow(MethodDef m) => new object[] {
      (long)m.MDToken.Raw,
      m.Name?.String ?? string.Empty,
      m.FullName,
      m.IsStatic,
      (long)m.Attributes,
    };

    // ---- Argument parsing ----------------------------------------------------------

    /// <summary>Reads a required string argument, or throws XERROR_BAD_ARGS.</summary>
    public static string GetString(object[] args, int index, string name) {
      if (index >= args.Length || args[index] is not string s) {
        throw new CommandException(Wire.ErrBadArgs, $"Missing or non-string argument '{name}'.");
      }
      return s;
    }

    /// <summary>Reads an optional string argument (null if absent/nil).</summary>
    public static string? GetOptString(object[] args, int index) =>
        index < args.Length ? args[index] as string : null;

    /// <summary>Reads an optional bool argument (default if absent).</summary>
    public static bool GetOptBool(object[] args, int index, bool @default) {
      if (index >= args.Length || args[index] is null) {
        return @default;
      }
      return args[index] is bool b ? b : @default;
    }

    /// <summary>Applies optional offset/limit paging to a sequence (limit &lt;= 0 means no limit).</summary>
    public static IEnumerable<T> Page<T>(IEnumerable<T> seq, long offset, long limit) {
      if (offset > 0) {
        seq = seq.Skip((int)offset);
      }
      if (limit > 0) {
        seq = seq.Take((int)limit);
      }
      return seq;
    }

    /// <summary>
    /// Envelopes a full result sequence as <c>[windowedRows, total]</c>, applying optional
    /// offset/limit to the window while <c>total</c> reports the full count (before windowing).
    /// </summary>
    public static object PageResult(IEnumerable<object> rows, long offset, long limit) {
      var all = rows as IList<object> ?? rows.ToList();
      return new object?[] { Page(all, offset, limit).ToArray(), (long)all.Count };
    }

    /// <summary>Reads an optional integer argument (any boxed numeric), or the default if absent.</summary>
    public static long GetOptInt64(object[] args, int index, long @default) {
      if (index >= args.Length || args[index] is null) {
        return @default;
      }
      try {
        return Convert.ToInt64(args[index]);
      } catch (Exception) {
        return @default;
      }
    }

    /// <summary>Reads a required integer argument (any boxed numeric), or throws.</summary>
    public static long GetInt64(object[] args, int index, string name) {
      if (index >= args.Length || args[index] is null) {
        throw new CommandException(Wire.ErrBadArgs, $"Missing argument '{name}'.");
      }
      try {
        return Convert.ToInt64(args[index]);
      } catch (Exception) {
        throw new CommandException(Wire.ErrBadArgs, $"Argument '{name}' is not an integer.");
      }
    }

    /// <summary>Reads a required metadata token argument as a uint.</summary>
    public static uint GetToken(object[] args, int index, string name) =>
        unchecked((uint)GetInt64(args, index, name));
  }

  /// <summary>A handler failure that maps to a specific XERROR_ code on the wire.</summary>
  internal sealed class CommandException : Exception {
    /// <summary>The XERROR_ code to send to the client.</summary>
    public string Code { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">An "XERROR_..." code.</param>
    /// <param name="message">A human-readable message.</param>
    public CommandException(string code, string message) : base(message) {
      Code = code;
    }
  }
}
