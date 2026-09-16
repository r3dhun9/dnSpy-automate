# dnSpy-automate

A dnSpy extension that turns dnSpy into a scriptable service. It runs a ZeroMQ server inside
the dnSpy process and exposes **109 commands and 13 events** over MessagePack, so a script can
load assemblies, read metadata, decompile, search, follow cross-references, edit IL, and drive
the .NET debugger — all without touching the GUI.

This is the server half. The companion [dnSpy-automate-pyclient](https://github.com/r3dhun9/dnSpy-automate-pyclient) is the intended way to talk
to it — one client method per command — and it also ships an MCP server for LLM-driven
analysis. The raw protocol is small enough to speak directly if you prefer (see below).

Idea motivated by [dariushoule/x64dbg-automate](https://github.com/dariushoule/x64dbg-automate).

## Why

dnSpy already has a decompiler, a metadata reader and a managed debugger that work on
obfuscated, PDB-less assemblies. What it does not have is a way to drive them from a script.
This extension adds that, keeping dnSpy's own engines as the source of truth rather than
reimplementing them.

## Requirements

- **.NET SDK 10** — and nothing else. **Building does not need dnSpy installed.**
- **dnSpyEx ≥ 5.0.0.0**, one of the **`dnSpy-net-*`** builds (not `dnSpy-netframework`), to
  run it in. Include the debugger extension if you want the debugger commands. Developed
  against dnSpyEx v6.6.0, the `net10` build.

The five `dnSpy.Contracts.*` assemblies have no NuGet package, so they are vendored in
`lib/` — unmodified binaries from the dnSpyEx v6.6.0 release, GPLv3, see [`lib/NOTICE`](lib/NOTICE).
Every other reference (`dnlib`, `MessagePack`, Roslyn, MEF, `VS.CoreUtility`) comes from
NuGet, pinned to the exact AssemblyVersion dnSpy 6.6.0 loads. All of them are compile-time
only: **NetMQ is the one dependency that actually ships**, and its closure is the only thing
installed alongside the extension.

## Build

```powershell
dotnet build -c Release -t:Package
```

That stages `artifacts\dnSpy-automate\` — ten files, exactly what gets installed and nothing
more (no `.pdb`, no satellite resource folders):

```
dnSpy-automate.x.dll                 the extension
dnSpy-automate.x.deps.json           how dnSpy's extension loader resolves the rest
NetMQ.dll  AsyncIO.dll  NaCl.dll     the ZeroMQ stack
System.ServiceModel.dll              NetMQ's transitive closure — dnSpy ships none of it
System.ServiceModel.Primitives.dll
System.Private.ServiceModel.dll
Microsoft.Extensions.ObjectPool.dll
Microsoft.Bcl.AsyncInterfaces.dll
```

## Install

Copy the whole `artifacts\dnSpy-automate\` folder into your dnSpy's `bin\Extensions\`:

```
dnSpy-net-win64\
  dnSpy.exe
  bin\
    Extensions\
      dnSpy-automate\        <- the staged folder goes here, name and all
        dnSpy-automate.x.dll
        ...
```

Three ways this goes wrong, all of them silent — dnSpy says nothing when it skips an
extension:

- **Copying only `dnSpy-automate.x.dll`.** The NetMQ closure is not optional and dnSpy does
  not ship it. Copy all ten files. (The extension catches this one and tells you.)
- **Installing into the .NET Framework dnSpy.** This builds `net10.0-windows`; that dnSpy
  cannot load it. Retarget the project to `net48` first.
- **dnSpy still running.** It holds an exclusive lock on a loaded extension, so the copy
  fails part-way. Close it first.

Or let the script do it, which checks all three plus the dnSpy version before copying:

```powershell
.\build-install.ps1                                     # build, then install into ..\dnSpy-net-win64\bin
.\build-install.ps1 -DnSpyBinDir C:\path\to\dnSpy\bin   # ...or into a dnSpy elsewhere
.\build-install.ps1 -NoInstall                          # build and stage only
```

It resolves the destination from `-DnSpyBinDir`, else `$env:DNSPY_BIN_DIR`, else
`..\dnSpy-net-win64\bin` next to the repo.

Launch dnSpy afterwards; the server starts on `AppLoaded` and stops on `AppExit`. If it
cannot start — unsupported dnSpy version, or an incomplete install — it says so once in a
dismissible dialog rather than failing silently.

### Building against a different dnSpy

The vendored contracts are v6.6.0. To compile against another dnSpy's instead — worth doing
if you run an older one, since a call to a contract member added after your version only
fails at run time — point the build at its `bin` directory:

```powershell
dotnet build -c Release -p:DnSpyContractsDir=C:\path\to\dnSpy\bin
$env:DNSPY_BIN_DIR = 'C:\path\to\dnSpy\bin'   # same effect, for the whole shell
```

`lib/` is then ignored. A missing or wrong directory is reported as one error naming the
files it could not find, not a few hundred `CS0246`s.

Three things about the project are not optional:

- `AssemblyName` must end in `.x` — dnSpy only discovers `*.x.dll` as extensions.
- Everything dnSpy loads itself is a compile-time-only reference (`Private=false` on the
  `lib/` references, `ExcludeAssets="runtime"` on the NuGet ones). Shipping a duplicate of
  any of them risks a conflicting assembly load.
- The project targets `net10.0-windows` only, matching the dnSpy build it is developed
  against.

## Connecting

On startup the server binds two random loopback ports (0xC000–0xFFFF, re-rolled on conflict)
and writes a discovery lockfile at `%TEMP%\dnspy_session.<pid>.lock` — three lines: REQ/REP
port, PUB/SUB port, bind address. A client scans that pattern to find running instances. The
lockfile is removed on a clean exit; a force-kill leaves a stale one behind, so clients reap
by PID liveness.

Only loopback binding is implemented. There is no remote mode, and no settings are read at
startup — `SessionSettings` is always constructed local.

Fastest check that it is alive, without the Python client:

```python
import glob, os, tempfile, msgpack, zmq
lock = glob.glob(os.path.join(tempfile.gettempdir(), "dnspy_session.*.lock"))[0]
port, _, addr = [l.strip() for l in open(lock) if l.strip()]
s = zmq.Context.instance().socket(zmq.REQ)
s.connect(f"tcp://{addr}:{port}")
s.send(msgpack.packb(["DA_REQ_COMPAT_VERSION"]))
print(msgpack.unpackb(s.recv(), raw=False))   # -> '0.0.1'
```

## What is exposed

Every command is a string constant in `src/Server/Wire.cs`, which is the authoritative list.
Handlers live one module per group under `src/Server/Commands/`, each with a static
`Register(table, ctx)`. Command names below drop the `DA_REQ_` prefix.

| Handler module | # | Commands |
|---|---|---|
| `InfraCommands` | 4 | `COMPAT_VERSION`, `DNSPY_PID`, `DNSPY_VERSION`, `DBG_IS_ELEVATED` — pure .NET, no dnSpy state |
| `HostCommands` | 2 | `QUIT` (async shutdown, so the reply flushes first), `GUI_REFRESH` |
| `SettingsCommands` | 2 | `READ_SETTING` / `WRITE_SETTING` by section GUID + key (`ISettingsService`, `"sz"` or `"uint"`) |
| `DocumentCommands` | 10 | load / unload / reload / list documents, module info, assembly refs, list decompilers, list types, decompile type & method (`IDsDocumentService`, `IDecompilerService`) |
| `MetadataCommands` | 13 | namespaces, nested types, methods, fields, properties, events, type & method info, parameters, custom attributes, token / name resolution (dnlib) |
| `IlCommands` | 1 | `GET_METHOD_IL` — instructions, locals and handlers in a form that round-trips through `REPLACE_METHOD_IL` |
| `DecompileCommands` | 6 | field / property / event, whole module, namespace, source↔IL span mapping (`IDecompiler`) |
| `SearchCommands` | 1 | `SEARCH` — `name` / `string` / `number` modes, optional regex and member-kind filter |
| `XrefCommands` | 6 | callers, callees, field access, derived types, implementors, overrides |
| `ResourceCommands` | 5 | embedded resources, `.resources` elements, `ldstr` literals |
| `BamlCommands` | 2 | list / decompile BAML (`IBamlDecompiler`, optional import) |
| `OutputCommands` | 3 | write / read / clear an output pane (`IOutputService`) |
| `TreeTabCommands` | 3 | navigate to a member, list tabs, active-tab text (`IDocumentTabService`) |
| `SaveCommands` | 1 | `SAVE_MODULE` — dnlib writers, managed or native |
| `EditCommands` | 6 | replace method IL, add field / method / type, remove member, C# method-body edit (dnlib + Roslyn) |
| `DebugSessionCommands` | 9 | start, attach, restart, detach-all, terminate-all, stop-all, is-debugging, is-running, `DBG_STATUS` |
| `DebugExecutionCommands` | 4 | break-all, run-all, run process, step |
| `DebugBreakpointCommands` | 6 | add / remove / enable / condition / list code breakpoints, plus `DBG_ADD_TRACEPOINT` |
| `DebugModuleBreakpointCommands` | 4 | add / remove / enable / list module-name breakpoints |
| `DebugInspectionCommands` | 10 | processes, threads, modules, read memory, current / freeze / thaw / switch thread, call stack, select frame |
| `DebugEvalCommands` | 9 | evaluate, locals, expand value, set value, exception settings, last exception, object IDs |
| `DebugDumpCommands` | 1 | `DBG_DUMP_MODULE` |
| `CommandHandlers` | 1 | `DA_REQ_BATCH` — many sub-requests in one round-trip |

A few notes on the less obvious ones:

- **Search and cross-references are reimplemented over dnlib.** dnSpy's own
  `IDocumentSearcherProvider` and its Analyzer engine are `internal`, so they cannot be
  referenced. The xref scans walk every loaded module by default; pass `scope="referencing"`
  to prune to modules that actually reference the target's assembly. `FIND_CALLERS` and
  `FIND_FIELD_ACCESS` emit one row per *call site*, each carrying its IL offset.
- **Save is done with dnlib directly.** dnSpy's `ISaveService` / `ITabSaver` are modal and
  internal. The managed writer recomputes each method's max-stack, which obfuscated bodies
  routinely defeat, so a failure is retried with `KeepOldMaxStack` before giving up. Pass
  `useNative=true` to patch the original image instead of rebuilding it.
- **Edits mutate the live `ModuleDef`.** Nothing touches disk until `SAVE_MODULE`. A member
  created by `ADD_FIELD` / `ADD_METHOD` / `ADD_TYPE` has no real metadata rid yet, so it gets
  a synthetic session token (rid from `0x00E00000`) that resolves for the rest of the
  session; unloading or reloading the document drops it.
- **Breakpoints need no PDB.** A breakpoint is module identity (from the on-disk dnlib
  module) plus a metadata token plus an IL offset, bound with `DbgILOffsetMapping.Exact`.
  `DBG_LIST_BREAKPOINTS` reports how many runtime locations each one bound to, which is what
  separates "never reached" from "never bound". A tracepoint is the same thing with a log
  message and, by default, `continue=true` so it never pauses.
- **`DBG_DUMP_MODULE`** writes a module out of a live debuggee — the recovery step for a
  packed assembly whose real metadata only exists after it has decrypted itself. It is keyed
  by load address (as reported by `DBG_LIST_MODULES`), not by name, and reports which
  strategy it used plus how many method bodies have an RVA outside the image, which is the
  tell for anti-tamper that relocates bodies rather than decrypting them in place.
- **`DBG_STATUS`** is the health probe: every field is a lock-guarded read, so it answers
  even when the debugger dispatcher is wedged, and its last field counts consecutive
  dispatch timeouts (0 = healthy).

### Events

Published on PUB/SUB as `[EVENT_NAME, ...fields]` with no topic prefix, so a subscriber must
subscribe to everything.

| Event | Fields |
|---|---|
| `EVENT_DEBUG_START` / `EVENT_DEBUG_STOP` | — |
| `EVENT_PAUSED` | `reason?` — `breakpoint`, `step`, `exception`, `program-break`, `entry-point`, `user-break`, `set-ip`, or nil |
| `EVENT_RESUMED` | — |
| `EVENT_PROCESS_CREATED` | `pid`, `name` |
| `EVENT_PROCESS_EXITED` | `pid`, `exitCode` |
| `EVENT_MODULE_LOADED` | `name`, `filename` |
| `EVENT_MODULE_UNLOADED` | `name` |
| `EVENT_THREAD_CREATED` / `EVENT_THREAD_EXITED` | `tid` |
| `EVENT_BREAKPOINT_HIT` | `bpId`, `tid?`, `moduleName?`, `token?`, `ilOffset?` |
| `EVENT_STEP_COMPLETE` | `tid?`, `error?` |
| `EVENT_EXCEPTION` | `name`, `message`, `isFirstChance`, `tid?` |

`EVENT_BREAKPOINT_HIT` comes from the per-breakpoint `Hit` event — "hit and the process
*will* pause" — so condition-false hits and continuing tracepoints do not emit it. The
location fields are nil for a non-.NET location.

## Wire protocol

- **Request** — one MessagePack frame, a positional array `[CMD_STRING, arg1, ...]`. The bare
  string `"PING"` is the one exception and answers `"PONG"`.
- **Response** — one MessagePack frame. Composite values are positional arrays; **element
  order is the contract**, there are no field names on the wire.
- **Error** — a 2-element array `["XERROR_...", "message"]`, delivered in-band rather than as
  a transport failure. Inside `DA_REQ_BATCH`, a failing sub-command becomes its own error
  tuple in the result array and does not abort the rest of the batch.
- **Paging** — list-shaped commands take trailing `offset, limit` arguments and answer
  `[rows, total]`, where `total` is the match count before windowing. `limit <= 0` (or
  absent) means no limit.
- **Arguments** — most static commands start with a `documentFilename`: the full path as
  reported by `LIST_DOCUMENTS`, matched case-insensitively. Members are addressed by
  metadata token (an integer). Optional arguments may be sent as nil or omitted entirely.
  Commands that decompile take a decompiler GUID from `LIST_DECOMPILERS`, or nil for dnSpy's
  current default.
- **Handshake** — `PING`/`PONG` plus `DA_REQ_COMPAT_VERSION`, currently **`0.0.1`**. The
  client compares it against its own compiled-in copy and refuses to continue on a mismatch.
  **Bump it whenever the wire changes** — appending a field to a row counts.

Error codes: `XERROR_UNK`, `XERROR_BAD_ARGS`, `XERROR_NOT_FOUND`, `XERROR_BAD_LOAD`,
`XERROR_DECOMPILE_FAILED`, `XERROR_UI_UNAVAILABLE`, `XERROR_UNAVAILABLE`,
`XERROR_SEARCH_FAILED`, `XERROR_SAVE_FAILED`, `XERROR_EDIT`, `XERROR_DBG`,
`XERROR_DBG_BUSY`, `XERROR_INTERNAL`.

`XERROR_UNAVAILABLE` is what the debugger commands return when dnSpy was built without the
debugger extension — the debugger services are optional MEF imports, and unless the whole set
resolves the extension runs without them, so the rest keeps working.

## Threading

This is the part that makes or breaks a command, because dnSpy has **three** threads that
matter and they are not interchangeable.

| Thread | Reached by | What belongs on it |
|---|---|---|
| ZMQ poller | — | receiving a request, encoding a reply, file I/O |
| WPF UI / Dispatcher | `ctx.RunOnUI(...)` | documents, decompilers, tree, tabs, output panes |
| dnSpy debugger dispatcher | `dbg.RunOnDbg(...)` | anything touching a `Dbg*` object |

Rules worth knowing before adding a debugger command:

- **`RunOnDbg` is bounded and cancellable.** It waits 10 seconds by default (30 for the
  evaluation commands, via `DebuggerServices.EvalDispatchTimeout`, because one func-eval can
  cost ~5s of dnSpy's own abort budget) and then answers `XERROR_DBG_BUSY`. It hands the
  delegate a `CancellationToken` that is cancelled if the caller gives up; pass it into every
  dnSpy evaluation API, because it is the only thing that can abort a hung function
  evaluation. A timed-out work item marks itself abandoned so client retries cannot pile up
  on a single-threaded queue.
- **Not everything needs the dispatcher.** `DbgManager.IsDebugging`, `IsRunning`, `Processes`
  and `BreakAll` are lock-guarded, and `StopDebuggingAll` / `TerminateAll` / `DetachAll` /
  `RunAll` marshal themselves. Those deliberately bypass `RunOnDbg` so status, break and stop
  still answer when a slow evaluation has the debugger thread busy.
- **Self-marshalling calls need a barrier.** Anything that `BeginInvoke`s internally returns
  *before* the work lands, so replying immediately is a lie. `DbgExceptionSettingsService.Modify`
  and the `CurrentThread` setter are followed by `dbg.DrainDbgQueue()`, which posts an empty
  work item and waits — the dispatcher is FIFO, so anything queued ahead of it has run. It
  must be called from *off* the dispatcher; on it, it runs inline and proves nothing. The
  session-control commands use the same idea with a 5-second cap
  (`DebugSessionCommands.Control`), where a barrier timeout is not an error — the request is
  simply still queued — because there the race is worse than a stale reply: dnSpy keeps
  formatting values out of a process whose memory is being unmapped, which faults on the
  engine thread and takes the whole process down.
- **Never call a dnSpy API that marshals onto the CorDebug engine thread from inside
  `RunOnDbg`.** `IDbgDotNetRuntime.GetRawModuleBytes` is one; it blocks with no timeout of its
  own, so it runs on a bounded task instead (`DebugDumpCommands`).

`DbgManager.Dispatcher` exposes only `CheckAccess` and `BeginInvoke` — no timeout, no cancel,
no way to query shutdown, and `BeginInvoke` silently no-ops once it has shut down. There is no
reset hook, so a genuinely wedged debugger thread can only be cleared by restarting dnSpy.
`DBG_STATUS` reports consecutive dispatch timeouts so a client can tell that apart from slow.

## Project layout

```
lib/                               vendored dnSpy.Contracts.* (v6.6.0, GPLv3) — compile-time
                                   only, so a clone builds with no dnSpy installed

src/AutomateExtension.cs           [ExportExtension] entry point; MEF imports; starts the server
src/Server/AutomationServer.cs     ZMQ sockets, poller thread, event queue, lockfile lifecycle
src/Server/CommandHandlers.cs      the dispatch table; DA_REQ_BATCH; exception -> error tuple
src/Server/CommandContext.cs       shared services, RunOnUI, arg parsing, row builders, paging,
                                   member resolution and the new-member session-token map
src/Server/DebuggerServices.cs     debugger MEF imports, RunOnDbg, DrainDbgQueue, timeout counter
src/Server/DebuggerEventBridge.cs  DbgManager events -> PUB frames
src/Server/Wire.cs                 every command / event / error string; the compat version
src/Server/SessionSettings.cs      port range, bind address, and the discovery lockfile
src/Server/UiThread.cs             WPF dispatcher marshalling

src/Server/Commands/               one module per group, each with a static Register(table, ctx)
    InfraCommands.cs  HostCommands.cs  SettingsCommands.cs
    DocumentCommands.cs  MetadataCommands.cs  IlCommands.cs  DecompileCommands.cs
    SearchCommands.cs  XrefCommands.cs  ResourceCommands.cs  BamlCommands.cs
    OutputCommands.cs  TreeTabCommands.cs  SaveCommands.cs  EditCommands.cs
    DebugSessionCommands.cs  DebugExecutionCommands.cs  DebugBreakpointCommands.cs
    DebugModuleBreakpointCommands.cs  DebugInspectionCommands.cs  DebugEvalCommands.cs
    DebugDumpCommands.cs
```

## Adding a command

1. Add the string constant to `src/Server/Wire.cs`, in the matching group.
2. Add a handler in the right `src/Server/Commands/*.cs` — parse the argument array with the
   `CommandContext.Get*` helpers, wrap any dnSpy access in `RunOnUI` or `RunOnDbg`, and return
   a positional array (list-shaped results go through `CommandContext.PageResult`).
3. Call the module's `Register` from `CommandHandlers`'s constructor if it is a new file.
4. **Bump `Wire.CompatVersion`** and mirror the change in the Python client's `wire.py`,
   models and command mixin. The handshake is exact string equality, so the two halves must
   ship together.

When appending to an existing row, append at the **end**. The client maps rows to models by
position, so an insert in the middle silently shifts every field after it.
