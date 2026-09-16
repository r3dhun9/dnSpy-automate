using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Settings;
using dnSpyAutomate.Server;

namespace dnSpyAutomate {
  /// <summary>
  /// dnSpy extension entry point. Pulls in the core services via MEF and starts/stops the
  /// ZMQ automation server on the app lifecycle events.
  /// </summary>
  [ExportExtension]
  sealed class AutomateExtension : IExtension {
    /// <summary>Minimum dnSpy version this extension supports (see App.xaml.cs gate).</summary>
    private static readonly Version MinDnSpyVersion = new Version(5, 0, 0, 0);

    /// <summary>Ignorable-message ids, so dnSpy can remember a "don't show again" per case.</summary>
    private static readonly Guid UnsupportedVersionMsgId = new Guid("22095c1d-8fe2-40d1-8da1-da26cc6d9da8");
    private static readonly Guid StartFailedMsgId = new Guid("9a25ab37-cbbf-4c49-ad79-0b0200da2aaf");

    private readonly IAppWindow _appWindow;
    private readonly IDsDocumentService _documentService;
    private readonly IDecompilerService _decompilerService;
    private readonly IDocumentTabService _tabService;
    private readonly IOutputService _outputService;
    private readonly ISettingsService _settingsService;
    private readonly IBamlDecompiler? _bamlDecompiler;

    // Debugger services — optional (null if the debugger extension isn't loaded).
    private readonly DbgManager? _dbgManager;
    private readonly DbgCodeBreakpointsService? _dbgCodeBreakpoints;
    private readonly DbgCodeBreakpointHitCountService? _dbgHitCount;
    private readonly DbgModuleBreakpointsService? _dbgModuleBreakpoints;
    private readonly DbgCallStackService? _dbgCallStack;
    private readonly DbgLanguageService? _dbgLanguages;
    private readonly DbgObjectIdService? _dbgObjectIds;
    private readonly DbgExceptionSettingsService? _dbgExceptionSettings;
    private readonly AttachableProcessesService? _dbgAttachable;
    private readonly DbgDotNetCodeLocationFactory? _dbgLocationFactory;

    private AutomationServer? _server;
    private DebuggerEventBridge? _eventBridge;

    /// <summary>MEF-injected constructor capturing the services the server needs.</summary>
    /// <param name="appWindow">Main window / dispatcher / version.</param>
    /// <param name="documentService">Loaded-documents manager.</param>
    /// <param name="decompilerService">Decompiler manager.</param>
    /// <param name="tabService">Tabs + treeview manager (navigation, active-tab content).</param>
    /// <param name="outputService">Output-pane manager.</param>
    /// <param name="bamlDecompiler">BAML→XAML decompiler; optional.</param>
    /// <remarks>The debugger services are optional imports so the extension still loads on a
    /// dnSpy build without the debugger extension: the static-analysis commands keep working
    /// and the debugger ones return XERROR_UNAVAILABLE.</remarks>
    [ImportingConstructor]
    public AutomateExtension(
        IAppWindow appWindow,
        IDsDocumentService documentService,
        IDecompilerService decompilerService,
        IDocumentTabService tabService,
        IOutputService outputService,
        ISettingsService settingsService,
        [Import(AllowDefault = true)] IBamlDecompiler? bamlDecompiler,
        [Import(AllowDefault = true)] DbgManager? dbgManager,
        [Import(AllowDefault = true)] DbgCodeBreakpointsService? dbgCodeBreakpoints,
        [Import(AllowDefault = true)] DbgCodeBreakpointHitCountService? dbgHitCount,
        [Import(AllowDefault = true)] DbgModuleBreakpointsService? dbgModuleBreakpoints,
        [Import(AllowDefault = true)] DbgCallStackService? dbgCallStack,
        [Import(AllowDefault = true)] DbgLanguageService? dbgLanguages,
        [Import(AllowDefault = true)] DbgObjectIdService? dbgObjectIds,
        [Import(AllowDefault = true)] DbgExceptionSettingsService? dbgExceptionSettings,
        [Import(AllowDefault = true)] AttachableProcessesService? dbgAttachable,
        [Import(AllowDefault = true)] DbgDotNetCodeLocationFactory? dbgLocationFactory) {
      _appWindow = appWindow;
      _documentService = documentService;
      _decompilerService = decompilerService;
      _tabService = tabService;
      _outputService = outputService;
      _settingsService = settingsService;
      _bamlDecompiler = bamlDecompiler;
      _dbgManager = dbgManager;
      _dbgCodeBreakpoints = dbgCodeBreakpoints;
      _dbgHitCount = dbgHitCount;
      _dbgModuleBreakpoints = dbgModuleBreakpoints;
      _dbgCallStack = dbgCallStack;
      _dbgLanguages = dbgLanguages;
      _dbgObjectIds = dbgObjectIds;
      _dbgExceptionSettings = dbgExceptionSettings;
      _dbgAttachable = dbgAttachable;
      _dbgLocationFactory = dbgLocationFactory;
    }

    /// <summary>No WPF resource dictionaries to merge.</summary>
    public IEnumerable<string> MergedResourceDictionaries {
      get { yield break; }
    }

    /// <summary>Metadata shown in dnSpy's extensions list.</summary>
    public ExtensionInfo ExtensionInfo => new ExtensionInfo {
      ShortDescription = "dnSpy-automate: ZMQ automation server",
      Copyright = "Copyright (c) r3dhun9",
    };

    /// <summary>Starts the server once the app is loaded; stops it on exit.</summary>
    /// <param name="event">The lifecycle event.</param>
    /// <param name="obj">Event-specific data (unused).</param>
    public void OnEvent(ExtensionEvent @event, object? obj) {
      switch (@event) {
        case ExtensionEvent.AppLoaded:
          StartServerOrReport();
          break;
        case ExtensionEvent.AppExit:
          _eventBridge?.Stop();
          _eventBridge = null;
          _server?.Stop();
          _server = null;
          break;
      }
    }

    /// <summary>
    /// Startup wrapper. A failure here is otherwise invisible — dnSpy reports nothing, the
    /// extension is simply inert, and the client just never finds a lockfile — so both ways
    /// this can fail are surfaced once as an ignorable message box.
    /// </summary>
    private void StartServerOrReport() {
      if (!IsSupportedVersion()) {
        Report(UnsupportedVersionMsgId,
            $"dnSpy-automate requires dnSpy {MinDnSpyVersion} or newer, but this dnSpy reports " +
            $"'{_appWindow.AssemblyInformationalVersion}'.\n\nThe automation server was not started.");
        return;
      }
      try {
        TryStartServer();
      }
      catch (Exception ex) {
        // An incomplete install lands here: the NetMQ closure is missing, so loading the types
        // TryStartServer touches throws as it is JITed. The usual cause is copying
        // dnSpy-automate.x.dll on its own instead of the whole extension folder.
        Report(StartFailedMsgId,
            $"dnSpy-automate failed to start its automation server.\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
            "If that names a missing assembly, the install is incomplete: copy the whole " +
            "extension folder — dnSpy-automate.x.dll, dnSpy-automate.x.deps.json, NetMQ.dll, " +
            "AsyncIO.dll, NaCl.dll and the System.ServiceModel.* / Microsoft.* set — into " +
            @"<dnSpy>\bin\Extensions\dnSpy-automate\.");
      }
    }

    /// <summary>
    /// Shows a message the user can permanently dismiss. Swallows the failure to show it:
    /// <see cref="MsgBox.Instance"/> throws when no message box service is registered, and
    /// there is nothing better to fall back to at this point in startup.
    /// </summary>
    /// <param name="id">Stable id for the "don't show again" setting.</param>
    /// <param name="message">Message to show.</param>
    private static void Report(Guid id, string message) {
      try {
        MsgBox.Instance.ShowIgnorableMessage(id, message);
      }
      catch {
        // Ignored — see summary.
      }
    }

    /// <summary>Builds the services and starts the server.</summary>
    private void TryStartServer() {
      var ui = new UiThread(_appWindow);
      var settings = SessionSettings.CreateLocal();
      var debugger = BuildDebuggerServices();
      var ctx = new CommandContext(
          ui, _appWindow, _documentService, _decompilerService,
          _tabService, _outputService, _settingsService, _bamlDecompiler, debugger);
      var handlers = new CommandHandlers(ctx);
      _server = new AutomationServer(handlers, settings);
      _server.Start();

      // Bridge debugger events → PUB stream once the server (and thus the PUB queue) is up.
      if (debugger is not null) {
        _eventBridge = new DebuggerEventBridge(debugger, _server);
        _eventBridge.Start();
      }
    }

    /// <summary>
    /// Bundles the debugger services if the full set imported; returns null otherwise (so the
    /// debugger commands cleanly report XERROR_UNAVAILABLE instead of half-working).
    /// </summary>
    private DebuggerServices? BuildDebuggerServices() {
      if (_dbgManager is null || _dbgCodeBreakpoints is null || _dbgHitCount is null ||
          _dbgModuleBreakpoints is null || _dbgCallStack is null || _dbgLanguages is null ||
          _dbgObjectIds is null || _dbgExceptionSettings is null || _dbgAttachable is null ||
          _dbgLocationFactory is null) {
        return null;
      }
      return new DebuggerServices(
          _dbgManager, _dbgCodeBreakpoints, _dbgHitCount, _dbgModuleBreakpoints, _dbgCallStack,
          _dbgLanguages, _dbgObjectIds, _dbgExceptionSettings, _dbgAttachable, _dbgLocationFactory);
    }

    /// <summary>
    /// Parses <see cref="IAppWindow.AssemblyInformationalVersion"/> and checks it is at least
    /// <see cref="MinDnSpyVersion"/>. Errs on the side of running if the version is unknown or
    /// unparseable.
    /// </summary>
    /// <returns>True if the server should start.</returns>
    private bool IsSupportedVersion() {
      var raw = _appWindow.AssemblyInformationalVersion;
      if (string.IsNullOrWhiteSpace(raw)) {
        return true;
      }
      // Informational versions may carry a suffix (e.g. "6.5.0-preview+abc123").
      var token = raw.Split(' ', '-', '+')[0];
      return !Version.TryParse(token, out var version) || version >= MinDnSpyVersion;
    }
  }
}
