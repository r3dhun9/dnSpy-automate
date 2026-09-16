using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;

namespace dnSpyAutomate.Server.Commands {
  /// <summary>Handshake and host-info commands (no dnSpy state access).</summary>
  internal static class InfraCommands {
    /// <summary>Registers this module's handlers into the dispatch table.</summary>
    public static void Register(IDictionary<string, Func<object[], object?>> t, CommandContext ctx) {
      t[Wire.ReqCompatVersion] = _ => Wire.CompatVersion;
      t[Wire.ReqDnSpyPid] = _ => Process.GetCurrentProcess().Id;
      t[Wire.ReqDnSpyVersion] = _ => ctx.AppWindow.AssemblyInformationalVersion ?? string.Empty;
      t[Wire.ReqDbgIsElevated] = _ => IsElevated();
    }

    /// <summary>Whether the dnSpy process runs with Administrator rights. Pure .NET, no dnSpy state.</summary>
    private static object IsElevated() {
      using var id = WindowsIdentity.GetCurrent();
      return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
  }
}
