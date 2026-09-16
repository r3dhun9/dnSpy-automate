using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Expression evaluation, locals enumeration, exception settings, and object IDs.</summary>
  internal static class DebugEvalCommands {
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // dnSpy's DbgEngineValueNodeImpl.GetChildrenCore catches *any* exception raised while fetching a
    // batch of children (its ExceptionUtils.IsInternalDebuggerError is true for everything except
    // OutOfMemory/OperationCanceled/ThreadAbort) and replaces the WHOLE batch with `count` identical
    // error nodes, hardcoding these two strings. So N identical rows mean one failure fanned out --
    // not N independent per-member failures. See CollapsedFanOutMessage.
    private const string FanOutErrorName = "<error>";
    private const string FanOutErrorExpression = "<expression>";

    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqDbgEvaluate] = a => Evaluate(ctx, a);
      t[Wire.ReqDbgGetLocals] = a => GetLocals(ctx, a);
      t[Wire.ReqDbgExpandValue] = a => ExpandValue(ctx, a);
      t[Wire.ReqDbgSetValue] = a => SetValue(ctx, a);
      t[Wire.ReqDbgSetException] = a => SetException(ctx, a);
      t[Wire.ReqDbgGetLastException] = _ => ctx.RequireDebugger().LastException;
      t[Wire.ReqDbgCreateObjectId] = a => CreateObjectId(ctx, a);
      t[Wire.ReqDbgListObjectIds] = a => ListObjectIds(ctx, a);
      t[Wire.ReqDbgDeleteObjectId] = a => DeleteObjectId(ctx, a);
    }

    // Args: expression, frameIndex?, funcEvalTimeoutMs?.
    // Returns [ok, valueText, typeText, error, threw]. When the expression's func-eval throws inside
    // the debuggee, dnSpy treats that as SUCCESS and hands back the thrown exception as the value --
    // so `threw` is the only way a caller can tell "here is your value" from "your call blew up".
    private static object Evaluate(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string expression = CommandContext.GetString(args, 0, "expression");
      int frameIndex = FrameIndex(args, 1);
      var funcEvalTimeout = FuncEvalTimeout(args, 2);
      return dbg.RunOnDbg(ct => {
        var eval = Setup(dbg, frameIndex, ct, funcEvalTimeout);
        try {
          var res = eval.Lang.ExpressionEvaluator.Evaluate(
              eval.Info, expression, DbgEvaluationOptions.Expression, null);
          if (res.Error is not null || res.Value is null) {
            return new object?[] {
              false, null, null, res.Error ?? "Expression produced no value.", false,
            };
          }
          string valueText = FormatValue(eval, res.Value);
          string typeText = FormatType(eval, res.Value);
          return new object?[] { true, valueText, typeText, null, res.IsThrownException };
        } finally {
          eval.Context.Close();
        }
      }, DebuggerServices.EvalDispatchTimeout);
    }

    // Args: frameIndex?, offset?, limit?, nodeOptions?, funcEvalTimeoutMs?.
    private static object GetLocals(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      int frameIndex = FrameIndex(args, 0);
      long offset = CommandContext.GetOptInt64(args, 1, 0);
      long limit = CommandContext.GetOptInt64(args, 2, 0);
      var nodeOptions = NodeOptions(args, 3);
      var funcEvalTimeout = FuncEvalTimeout(args, 4);
      return dbg.RunOnDbg(ct => {
        var eval = Setup(dbg, frameIndex, ct, funcEvalTimeout);
        try {
          // Formatting a value can call ToString() in the debuggee, so nodeOptions matters here too:
          // NoFuncEval (1) makes reading locals safe on a thread you do not want to run.
          var nodes = eval.Lang.LocalsProvider.GetNodes(
              eval.Info, nodeOptions, default);
          var rows = new List<object>();
          foreach (var info in nodes) {
            var node = info.ValueNode;
            var nameW = new CapturingDbgTextWriter();
            node.FormatName(eval.Info, nameW, default, Culture);
            var valW = new CapturingDbgTextWriter();
            node.FormatValue(eval.Info, valW, default, Culture);
            var typeW = new CapturingDbgTextWriter();
            node.FormatExpectedType(eval.Info, typeW, default, default, Culture);
            // Include node.Expression so a local can be fed straight to EXPAND_VALUE/SET_VALUE.
            rows.Add(new object?[] {
              info.Kind.ToString(), nameW.ToString(), valW.ToString(), typeW.ToString(),
              node.Expression, node.HasChildren == true,
            });
          }
          return CommandContext.PageResult(rows, offset, limit);
        } finally {
          eval.Context.Close();
        }
      }, DebuggerServices.EvalDispatchTimeout);
    }

    // Args: expression, frameIndex?, startIndex?, count?, nodeOptions?, funcEvalTimeoutMs?.
    // Expression-based (value nodes are transient, closed when the runtime continues), so the SOURCE
    // EXPRESSION IS RE-EVALUATED on every call and every page -- a side-effecting expression runs
    // again each time. Capture it once with DBG_CREATE_OBJECT_ID and expand $N to avoid that.
    // Returns [ok, childCount, rows, error] where each row is [name, value, type, expression, hasChildren].
    private static object ExpandValue(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string expression = CommandContext.GetString(args, 0, "expression");
      int frameIndex = FrameIndex(args, 1);
      ulong startIndex = (ulong)CommandContext.GetOptInt64(args, 2, 0);
      int count = (int)CommandContext.GetOptInt64(args, 3, 0);
      var nodeOptions = NodeOptions(args, 4);
      var funcEvalTimeout = FuncEvalTimeout(args, 5);
      var evalOptions = DbgEvaluationOptions.Expression;
      if ((nodeOptions & DbgValueNodeEvaluationOptions.NoFuncEval) != 0) {
        evalOptions |= DbgEvaluationOptions.NoFuncEval;
      }
      return dbg.RunOnDbg(ct => {
        var eval = Setup(dbg, frameIndex, ct, funcEvalTimeout);
        try {
          var create = eval.Lang.ValueNodeFactory.Create(
              eval.Info, expression, nodeOptions, evalOptions, null);
          var node = create.ValueNode;
          if (node is null) {
            return new object?[] { false, 0L, Array.Empty<object>(), "Expression produced no value." };
          }
          if (node.ErrorMessage is not null) {
            return new object?[] { false, 0L, Array.Empty<object>(), node.ErrorMessage };
          }
          ulong total = node.GetChildCount(eval.Info);
          int available = total > (ulong)int.MaxValue ? int.MaxValue : (int)total;
          int start = startIndex > (ulong)int.MaxValue ? int.MaxValue : (int)startIndex;
          int take = 0;
          if (start < available) {
            int remaining = available - start;
            int want = count > 0 ? count : Math.Min(remaining, 1000);  // default page when unspecified
            take = Math.Min(want, remaining);
          }
          var children = take > 0
              ? node.GetChildren(eval.Info, (ulong)start, take, nodeOptions)
              : Array.Empty<DbgValueNode>();
          var rows = new List<object>();
          bool allFannedOut = children.Length > 0;
          foreach (var child in children) {
            var nameW = new CapturingDbgTextWriter();
            child.FormatName(eval.Info, nameW, default, Culture);
            var valW = new CapturingDbgTextWriter();
            child.FormatValue(eval.Info, valW, default, Culture);
            var typeW = new CapturingDbgTextWriter();
            child.FormatExpectedType(eval.Info, typeW, default, default, Culture);
            string name = nameW.ToString();
            allFannedOut &= name == FanOutErrorName && child.Expression == FanOutErrorExpression;
            rows.Add(new object?[] {
              name, valW.ToString(), typeW.ToString(), child.Expression, child.HasChildren == true,
            });
          }
          if (allFannedOut) {
            return new object?[] {
              false, (long)total, Array.Empty<object>(), CollapsedFanOutMessage(rows.Count),
            };
          }
          return new object?[] { true, (long)total, rows.ToArray(), null };
        } finally {
          eval.Context.Close();
        }
      }, DebuggerServices.EvalDispatchTimeout);
    }

    /// <summary>Explains a whole child batch that dnSpy replaced with identical error nodes.</summary>
    /// <param name="childCount">How many error rows dnSpy produced.</param>
    /// <returns>The message for the failed EXPAND_VALUE response.</returns>
    private static string CollapsedFanOutMessage(int childCount) =>
        $"dnSpy reported one internal evaluation error for the whole batch of {childCount} children, " +
        "so none of them holds a real value. This almost always means the source expression itself " +
        "could not be evaluated -- most often a func-eval that threw or timed out -- rather than " +
        "anything wrong with the individual members. Capture the value once with " +
        "DBG_CREATE_OBJECT_ID and expand $N instead, or pass nodeOptions=1 (NoFuncEval) to read " +
        "fields without running any code in the debuggee.";

    // Args: expression (lhs target), value (rhs source expression), frameIndex?, funcEvalTimeoutMs?.
    private static object SetValue(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string expression = CommandContext.GetString(args, 0, "expression");
      string value = CommandContext.GetString(args, 1, "value");
      int frameIndex = FrameIndex(args, 2);
      var funcEvalTimeout = FuncEvalTimeout(args, 3);
      return dbg.RunOnDbg(ct => {
        var eval = Setup(dbg, frameIndex, ct, funcEvalTimeout);
        try {
          var r = eval.Lang.ExpressionEvaluator.Assign(
              eval.Info, expression, value, DbgEvaluationOptions.Expression);
          if (r.Error is not null) {
            throw new CommandException(Wire.ErrDbg, r.Error);
          }
          return (object)true;
        } finally {
          eval.Context.Close();
        }
      }, DebuggerServices.EvalDispatchTimeout);
    }

    private static object SetException(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string name = CommandContext.GetString(args, 0, "name");
      bool first = CommandContext.GetOptBool(args, 1, true);
      bool second = CommandContext.GetOptBool(args, 2, false);
      var flags = DbgExceptionDefinitionFlags.None;
      if (first) {
        flags |= DbgExceptionDefinitionFlags.StopFirstChance;
      }
      if (second) {
        flags |= DbgExceptionDefinitionFlags.StopSecondChance;
      }
      var id = new DbgExceptionId(PredefinedExceptionCategories.DotNet, name);
      // Modify() is safe from any thread -- it unconditionally BeginInvoke's onto the debugger
      // dispatcher -- but it returns before the change has landed, so a client that sets a filter and
      // immediately resumes would race it. Wrapping the call in RunOnDbg would not help: Modify posts
      // its own work item *behind* ours, so ours would return first anyway. A FIFO barrier after the
      // call is what actually makes the acknowledgement mean something.
      dbg.ExceptionSettings.Modify(id, new DbgExceptionSettings(flags));
      dbg.DrainDbgQueue();
      return true;
    }

    // Args: expression, frameIndex?, funcEvalTimeoutMs?.
    private static object CreateObjectId(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      string expression = CommandContext.GetString(args, 0, "expression");
      int frameIndex = FrameIndex(args, 1);
      var funcEvalTimeout = FuncEvalTimeout(args, 2);
      return dbg.RunOnDbg(ct => {
        var eval = Setup(dbg, frameIndex, ct, funcEvalTimeout);
        try {
          var res = eval.Lang.ExpressionEvaluator.Evaluate(
              eval.Info, expression, DbgEvaluationOptions.Expression, null);
          if (res.Error is not null || res.Value is null) {
            throw new CommandException(Wire.ErrDbg, res.Error ?? "Expression produced no value.");
          }
          // A thrown exception is still a real heap object worth tracking, so no error here -- use
          // DBG_EVALUATE's `threw` flag if the caller needs to know which it got.
          var objId = dbg.ObjectIds.CreateObjectId(res.Value)
              ?? throw new CommandException(Wire.ErrDbg, "Cannot create an object id for that value.");
          return new object[] { (long)objId.Id };
        } finally {
          eval.Context.Close();
        }
      }, DebuggerServices.EvalDispatchTimeout);
    }

    private static object ListObjectIds(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      long offset = CommandContext.GetOptInt64(args, 0, 0);
      long limit = CommandContext.GetOptInt64(args, 1, 0);
      return dbg.RunOnDbg(() => {
        var runtime = dbg.CurrentRuntime;
        var rows = runtime is null ? Enumerable.Empty<object>()
            : dbg.ObjectIds.GetObjectIds(runtime).Select(o => (object)new object?[] { (long)o.Id });
        return CommandContext.PageResult(rows, offset, limit);
      });
    }

    private static object DeleteObjectId(CommandContext ctx, object[] args) {
      var dbg = ctx.RequireDebugger();
      uint id = CommandContext.GetToken(args, 0, "id");
      return dbg.RunOnDbg(() => {
        var runtime = dbg.CurrentRuntime;
        if (runtime is null) {
          return false;
        }
        var objId = dbg.ObjectIds.GetObjectId(runtime, id);
        if (objId is null) {
          return false;
        }
        objId.Remove();
        return true;
      });
    }

    // ---- Argument helpers ---------------------------------------------------------

    /// <summary>Reads an optional frame index, defaulting to the innermost frame.</summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="index">Position of the frame-index argument.</param>
    /// <returns>The requested frame index, or 0.</returns>
    private static int FrameIndex(object[] args, int index) =>
        args.Length > index && args[index] is not null
            ? (int)CommandContext.GetInt64(args, index, "frameIndex")
            : 0;

    /// <summary>
    /// Reads an optional raw <see cref="DbgValueNodeEvaluationOptions"/> bitmask, shipped as an int the
    /// same way type/method <c>attributes</c> already are. The flags are <c>NoFuncEval=1</c>,
    /// <c>ResultsView=2</c>, <c>DynamicView=4</c>, <c>RawView=8</c>,
    /// <c>HideCompilerGeneratedMembers=16</c>, <c>RespectHideMemberAttributes=32</c>,
    /// <c>PublicMembers=64</c>.
    ///
    /// <c>NoFuncEval</c> is the one that matters for hostile code. The default of 0 leaves func-eval
    /// on, which is what makes property getters readable — and also what makes a protected object's
    /// 30-odd getters each run code in the debuggee, which is how one expansion turns into dozens of
    /// func-evals and then into a wedged dispatcher.
    /// </summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="index">Position of the options argument.</param>
    /// <returns>The requested flags, or None.</returns>
    private static DbgValueNodeEvaluationOptions NodeOptions(object[] args, int index) =>
        (DbgValueNodeEvaluationOptions)CommandContext.GetOptInt64(args, index, 0);

    /// <summary>
    /// Reads an optional func-eval budget. A default <see cref="TimeSpan"/> tells dnSpy to use its
    /// own <c>DbgLanguage.DefaultFuncEvalTimeout</c>, which is only <b>one second</b> — too short for
    /// a real decryption or initialisation routine. Raising it matters more than it looks: once a
    /// single func-eval times out, dnSpy latches <c>FuncEvalTimedOutNowDisabled</c> on the
    /// ContinueContext and *every* later evaluation fails until the debuggee continues.
    /// </summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="index">Position of the timeout argument, in milliseconds.</param>
    /// <returns>The requested budget, or default to use dnSpy's own.</returns>
    private static TimeSpan FuncEvalTimeout(object[] args, int index) {
      long ms = CommandContext.GetOptInt64(args, index, 0);
      return ms > 0 ? TimeSpan.FromMilliseconds(ms) : default;
    }

    // ---- Evaluation setup + formatting --------------------------------------------

    private readonly struct EvalBundle {
      public EvalBundle(DbgLanguage lang, DbgEvaluationContext context, DbgEvaluationInfo info) {
        Lang = lang;
        Context = context;
        Info = info;
      }
      public DbgLanguage Lang { get; }
      public DbgEvaluationContext Context { get; }
      public DbgEvaluationInfo Info { get; }
    }

    /// <summary>
    /// Builds the per-call evaluation context. The cancellation token must be the one
    /// <see cref="DebuggerServices.RunOnDbg{T}(Func{CancellationToken, T}, TimeSpan?)"/> handed us:
    /// dnSpy checks it all through its evaluation and value-node paths, and cancelling it is the only
    /// way to abort a func-eval that has hung. Passing CancellationToken.None here is what used to
    /// let one bad func-eval wedge the whole dispatcher.
    /// </summary>
    /// <param name="dbg">The debugger services.</param>
    /// <param name="frameIndex">Which frame to evaluate in; 0 is the innermost.</param>
    /// <param name="ct">Cancelled when the caller gives up waiting.</param>
    /// <param name="funcEvalTimeout">Func-eval budget, or default for dnSpy's own.</param>
    /// <returns>The language, context and evaluation info for this call.</returns>
    private static EvalBundle Setup(
        DebuggerServices dbg, int frameIndex, CancellationToken ct, TimeSpan funcEvalTimeout) {
      var thread = dbg.CurrentThread
          ?? throw new CommandException(Wire.ErrDbg, "No current thread (is the debuggee paused?).");
      var frame = dbg.GetFrame(thread, frameIndex);
      if (frame is null) {
        // A thread with *no* frames at all is not the same failure as asking for frame 12 of 3,
        // and it is by far the more confusing one: it is what dnSpy leaves behind after it
        // aborts an evaluation — a func-eval that ran out of budget, or one that tripped a
        // breakpoint in the method it was invoking. The frames come back when the debuggee is
        // resumed and stopped again; switching threads does not rebuild them.
        bool noFramesAtAll = thread.GetFrames(1).Length == 0;
        throw new CommandException(
            Wire.ErrDbg,
            noFramesAtAll
                ? "The current thread has no stack frames. That normally means a previous " +
                  "evaluation was aborted — a func-eval that exceeded its budget, or one that " +
                  "hit a breakpoint in the method it was calling. Resume and stop again to " +
                  "rebuild the frames; switching threads will not."
                : $"No stack frame at index {frameIndex}.");
      }
      var lang = dbg.Languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
      var context = lang.CreateContext(frame, DbgEvaluationContextOptions.None, funcEvalTimeout, ct);
      var info = new DbgEvaluationInfo(context, frame, ct);
      return new EvalBundle(lang, context, info);
    }

    private static string FormatValue(EvalBundle eval, DbgValue value) {
      var w = new CapturingDbgTextWriter();
      eval.Lang.Formatter.FormatValue(eval.Info, w, value, default, Culture);
      return w.ToString();
    }

    private static string FormatType(EvalBundle eval, DbgValue value) {
      var w = new CapturingDbgTextWriter();
      eval.Lang.Formatter.FormatType(eval.Info, w, value, default, Culture);
      return w.ToString();
    }
  }
}
