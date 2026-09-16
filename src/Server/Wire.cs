using System;
using MessagePack;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Wire-protocol constants and MessagePack encode/decode helpers.
  ///
  /// The protocol mirrors x64dbg-automate so a sibling Python client can reuse the same
  /// transport conventions:
  ///   - Request:  MessagePack [CMD_STRING, arg1, ...]; the bare string "PING" is special.
  ///   - Response: an arbitrary MessagePack value; structs are POSITIONAL arrays (field
  ///               order is the contract), never maps.
  ///   - Error:    a 2-element array ["XERROR_...", "message"].
  /// Values are (de)serialized through MessagePack's primitive-object graph so the wire
  /// bytes match Python's <c>msgpack</c>: string→str, byte[]→bin, arrays→positional
  /// arrays, integers→int, etc.
  /// </summary>
  internal static class Wire {
    /// <summary>
    /// Handshake compatibility token. The client compares this against its own compiled-in
    /// copy after connecting. Bump it whenever the wire protocol changes in any way.
    /// </summary>
    public const string CompatVersion = "0.0.1";

    // ---- Request command identifiers (the string equals the constant name's value). ----
    public const string Ping = "PING";
    public const string Pong = "PONG";

    // Infra
    public const string ReqCompatVersion = "DA_REQ_COMPAT_VERSION";
    public const string ReqDnSpyPid = "DA_REQ_DNSPY_PID";
    public const string ReqDnSpyVersion = "DA_REQ_DNSPY_VERSION";
    public const string ReqBatch = "DA_REQ_BATCH";
    public const string ReqQuit = "DA_REQ_QUIT";
    public const string ReqGuiRefresh = "DA_REQ_GUI_REFRESH";
    public const string ReqReadSetting = "DA_REQ_READ_SETTING";
    public const string ReqWriteSetting = "DA_REQ_WRITE_SETTING";

    // Documents / decompilers
    public const string ReqListDocuments = "DA_REQ_LIST_DOCUMENTS";
    public const string ReqLoadDocument = "DA_REQ_LOAD_DOCUMENT";
    public const string ReqUnloadDocument = "DA_REQ_UNLOAD_DOCUMENT";
    public const string ReqListDecompilers = "DA_REQ_LIST_DECOMPILERS";
    public const string ReqListTypes = "DA_REQ_LIST_TYPES";
    public const string ReqReloadDocument = "DA_REQ_RELOAD_DOCUMENT";
    public const string ReqGetModuleInfo = "DA_REQ_GET_MODULE_INFO";
    public const string ReqListAssemblyRefs = "DA_REQ_LIST_ASSEMBLY_REFS";

    // Metadata
    public const string ReqListNamespaces = "DA_REQ_LIST_NAMESPACES";
    public const string ReqListNestedTypes = "DA_REQ_LIST_NESTED_TYPES";
    public const string ReqListMethods = "DA_REQ_LIST_METHODS";
    public const string ReqListFields = "DA_REQ_LIST_FIELDS";
    public const string ReqListProperties = "DA_REQ_LIST_PROPERTIES";
    public const string ReqListEvents = "DA_REQ_LIST_EVENTS";
    public const string ReqGetTypeInfo = "DA_REQ_GET_TYPE_INFO";
    public const string ReqGetMethodInfo = "DA_REQ_GET_METHOD_INFO";
    public const string ReqListParameters = "DA_REQ_LIST_PARAMETERS";
    public const string ReqListCustomAttributes = "DA_REQ_LIST_CUSTOM_ATTRIBUTES";
    public const string ReqResolveToken = "DA_REQ_RESOLVE_TOKEN";
    public const string ReqFindType = "DA_REQ_FIND_TYPE";
    public const string ReqFindMethod = "DA_REQ_FIND_METHOD";
    public const string ReqGetMethodIl = "DA_REQ_GET_METHOD_IL";

    // Decompilation
    public const string ReqDecompileType = "DA_REQ_DECOMPILE_TYPE";
    public const string ReqDecompileMethod = "DA_REQ_DECOMPILE_METHOD";
    public const string ReqDecompileField = "DA_REQ_DECOMPILE_FIELD";
    public const string ReqDecompileProperty = "DA_REQ_DECOMPILE_PROPERTY";
    public const string ReqDecompileEvent = "DA_REQ_DECOMPILE_EVENT";
    public const string ReqDecompileModule = "DA_REQ_DECOMPILE_MODULE";
    public const string ReqDecompileNamespace = "DA_REQ_DECOMPILE_NAMESPACE";
    public const string ReqGetIlMapping = "DA_REQ_GET_IL_MAPPING";

    // Search
    public const string ReqSearch = "DA_REQ_SEARCH";

    // Cross-references
    public const string ReqFindCallers = "DA_REQ_FIND_CALLERS";
    public const string ReqFindCallees = "DA_REQ_FIND_CALLEES";
    public const string ReqFindFieldAccess = "DA_REQ_FIND_FIELD_ACCESS";
    public const string ReqFindDerivedTypes = "DA_REQ_FIND_DERIVED_TYPES";
    public const string ReqFindImplementors = "DA_REQ_FIND_IMPLEMENTORS";
    public const string ReqFindOverrides = "DA_REQ_FIND_OVERRIDES";

    // Resources / strings
    public const string ReqListResources = "DA_REQ_LIST_RESOURCES";
    public const string ReqReadResource = "DA_REQ_READ_RESOURCE";
    public const string ReqListResourceElements = "DA_REQ_LIST_RESOURCE_ELEMENTS";
    public const string ReqReadResourceElement = "DA_REQ_READ_RESOURCE_ELEMENT";
    public const string ReqListStrings = "DA_REQ_LIST_STRINGS";

    // BAML
    public const string ReqListBaml = "DA_REQ_LIST_BAML";
    public const string ReqDecompileBaml = "DA_REQ_DECOMPILE_BAML";

    // Output panes
    public const string ReqOutputWrite = "DA_REQ_OUTPUT_WRITE";
    public const string ReqOutputRead = "DA_REQ_OUTPUT_READ";
    public const string ReqOutputClear = "DA_REQ_OUTPUT_CLEAR";

    // Tree / tabs
    public const string ReqNavigateTo = "DA_REQ_NAVIGATE_TO";
    public const string ReqListTabs = "DA_REQ_LIST_TABS";
    public const string ReqGetActiveTabText = "DA_REQ_GET_ACTIVE_TAB_TEXT";

    // Save / export
    public const string ReqSaveModule = "DA_REQ_SAVE_MODULE";

    // Editing (IL editor / add-remove members / C# method-body edit)
    public const string ReqReplaceMethodIl = "DA_REQ_REPLACE_METHOD_IL";
    public const string ReqAddField = "DA_REQ_ADD_FIELD";
    public const string ReqAddMethod = "DA_REQ_ADD_METHOD";
    public const string ReqAddType = "DA_REQ_ADD_TYPE";
    public const string ReqRemoveMember = "DA_REQ_REMOVE_MEMBER";
    public const string ReqEditMethodCsharp = "DA_REQ_EDIT_METHOD_CSHARP";

    // ---- Debugger (DA_REQ_DBG_*) ----
    // Session
    public const string ReqDbgStart = "DA_REQ_DBG_START";
    public const string ReqDbgAttach = "DA_REQ_DBG_ATTACH";
    public const string ReqDbgRestart = "DA_REQ_DBG_RESTART";
    public const string ReqDbgDetachAll = "DA_REQ_DBG_DETACH_ALL";
    public const string ReqDbgTerminateAll = "DA_REQ_DBG_TERMINATE_ALL";
    public const string ReqDbgStopAll = "DA_REQ_DBG_STOP_ALL";
    public const string ReqDbgIsDebugging = "DA_REQ_DBG_IS_DEBUGGING";
    public const string ReqDbgIsRunning = "DA_REQ_DBG_IS_RUNNING";
    public const string ReqDbgStatus = "DA_REQ_DBG_STATUS";
    public const string ReqDbgIsElevated = "DA_REQ_DBG_IS_ELEVATED";
    // Execution
    public const string ReqDbgBreakAll = "DA_REQ_DBG_BREAK_ALL";
    public const string ReqDbgRunAll = "DA_REQ_DBG_RUN_ALL";
    public const string ReqDbgRunProcess = "DA_REQ_DBG_RUN_PROCESS";
    public const string ReqDbgStep = "DA_REQ_DBG_STEP";
    // Breakpoints
    public const string ReqDbgAddBreakpoint = "DA_REQ_DBG_ADD_BREAKPOINT";
    public const string ReqDbgRemoveBreakpoint = "DA_REQ_DBG_REMOVE_BREAKPOINT";
    public const string ReqDbgEnableBreakpoint = "DA_REQ_DBG_ENABLE_BREAKPOINT";
    public const string ReqDbgSetBreakpointCondition = "DA_REQ_DBG_SET_BREAKPOINT_CONDITION";
    public const string ReqDbgListBreakpoints = "DA_REQ_DBG_LIST_BREAKPOINTS";
    // Module breakpoints
    public const string ReqDbgAddModuleBreakpoint = "DA_REQ_DBG_ADD_MODULE_BREAKPOINT";
    public const string ReqDbgRemoveModuleBreakpoint = "DA_REQ_DBG_REMOVE_MODULE_BREAKPOINT";
    public const string ReqDbgEnableModuleBreakpoint = "DA_REQ_DBG_ENABLE_MODULE_BREAKPOINT";
    public const string ReqDbgListModuleBreakpoints = "DA_REQ_DBG_LIST_MODULE_BREAKPOINTS";
    public const string ReqDbgAddTracepoint = "DA_REQ_DBG_ADD_TRACEPOINT";
    // Processes / threads / modules / memory
    public const string ReqDbgListProcesses = "DA_REQ_DBG_LIST_PROCESSES";
    public const string ReqDbgListThreads = "DA_REQ_DBG_LIST_THREADS";
    public const string ReqDbgListModules = "DA_REQ_DBG_LIST_MODULES";
    public const string ReqDbgReadMemory = "DA_REQ_DBG_READ_MEMORY";
    public const string ReqDbgDumpModule = "DA_REQ_DBG_DUMP_MODULE";
    public const string ReqDbgCurrentThread = "DA_REQ_DBG_CURRENT_THREAD";
    public const string ReqDbgFreezeThread = "DA_REQ_DBG_FREEZE_THREAD";
    public const string ReqDbgThawThread = "DA_REQ_DBG_THAW_THREAD";
    public const string ReqDbgSwitchThread = "DA_REQ_DBG_SWITCH_THREAD";
    // Call stack
    public const string ReqDbgGetCallstack = "DA_REQ_DBG_GET_CALLSTACK";
    public const string ReqDbgSelectFrame = "DA_REQ_DBG_SELECT_FRAME";
    // Evaluation / locals
    public const string ReqDbgEvaluate = "DA_REQ_DBG_EVALUATE";
    public const string ReqDbgGetLocals = "DA_REQ_DBG_GET_LOCALS";
    public const string ReqDbgExpandValue = "DA_REQ_DBG_EXPAND_VALUE";
    public const string ReqDbgSetValue = "DA_REQ_DBG_SET_VALUE";
    // Exceptions
    public const string ReqDbgSetException = "DA_REQ_DBG_SET_EXCEPTION";
    public const string ReqDbgGetLastException = "DA_REQ_DBG_GET_LAST_EXCEPTION";
    // Object IDs
    public const string ReqDbgCreateObjectId = "DA_REQ_DBG_CREATE_OBJECT_ID";
    public const string ReqDbgListObjectIds = "DA_REQ_DBG_LIST_OBJECT_IDS";
    public const string ReqDbgDeleteObjectId = "DA_REQ_DBG_DELETE_OBJECT_ID";

    // ---- PUB/SUB event names (frame = [EVENT_NAME, *fields]) ----
    public const string EvtDebugStart = "EVENT_DEBUG_START";
    public const string EvtDebugStop = "EVENT_DEBUG_STOP";
    public const string EvtPaused = "EVENT_PAUSED";
    public const string EvtResumed = "EVENT_RESUMED";
    public const string EvtProcessCreated = "EVENT_PROCESS_CREATED";
    public const string EvtProcessExited = "EVENT_PROCESS_EXITED";
    public const string EvtModuleLoaded = "EVENT_MODULE_LOADED";
    public const string EvtModuleUnloaded = "EVENT_MODULE_UNLOADED";
    public const string EvtThreadCreated = "EVENT_THREAD_CREATED";
    public const string EvtThreadExited = "EVENT_THREAD_EXITED";
    public const string EvtBreakpointHit = "EVENT_BREAKPOINT_HIT";
    public const string EvtStepComplete = "EVENT_STEP_COMPLETE";
    public const string EvtException = "EVENT_EXCEPTION";

    // ---- Error codes (element 0 of an error tuple always starts with "XERROR_"). ----
    public const string ErrUnknown = "XERROR_UNK";
    public const string ErrBadArgs = "XERROR_BAD_ARGS";
    public const string ErrNotFound = "XERROR_NOT_FOUND";
    public const string ErrBadLoad = "XERROR_BAD_LOAD";
    public const string ErrDecompileFailed = "XERROR_DECOMPILE_FAILED";
    public const string ErrUiUnavailable = "XERROR_UI_UNAVAILABLE";
    public const string ErrUnavailable = "XERROR_UNAVAILABLE";
    public const string ErrSearchFailed = "XERROR_SEARCH_FAILED";
    public const string ErrSaveFailed = "XERROR_SAVE_FAILED";
    public const string ErrEdit = "XERROR_EDIT";
    public const string ErrDbg = "XERROR_DBG";
    /// <summary>
    /// The debugger dispatcher did not answer in time. Distinct from <see cref="ErrDbg"/> because it
    /// is the one debugger failure where retrying, or tearing the session down, is a sensible
    /// programmatic response — and because a client should never have to string-match a message to
    /// find that out.
    /// </summary>
    public const string ErrDbgBusy = "XERROR_DBG_BUSY";
    public const string ErrInternal = "XERROR_INTERNAL";

    /// <summary>Serializes a response value to MessagePack bytes.</summary>
    /// <param name="value">
    /// A primitive-object graph: null, string, bool, integer/double, byte[], or
    /// object[] of the same (nested). Anything else will throw at serialization time.
    /// </param>
    /// <returns>The MessagePack-encoded bytes.</returns>
    public static byte[] Encode(object? value) =>
        MessagePackSerializer.Serialize<object?>(value);

    /// <summary>Deserializes a request frame into a primitive-object graph.</summary>
    /// <param name="data">The MessagePack-encoded request bytes.</param>
    /// <returns>
    /// A string (bare command such as "PING"), an <c>object[]</c> ([cmd, args...]), or a
    /// primitive scalar; matches the msgpack token that was on the wire.
    /// </returns>
    public static object? Decode(byte[] data) =>
        MessagePackSerializer.Deserialize<object?>(data);

    /// <summary>Builds a standard error tuple ["XERROR_...", message].</summary>
    /// <param name="code">The error code; must start with "XERROR_".</param>
    /// <param name="message">A human-readable message.</param>
    /// <returns>A 2-element object array ready to <see cref="Encode"/>.</returns>
    public static object[] Error(string code, string message) =>
        new object[] { code, message };
  }
}
