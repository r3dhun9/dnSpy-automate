using System;
using System.Diagnostics;
using System.IO;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Session configuration and the on-disk session lockfile used for client discovery.
  ///
  /// This increment supports local mode only: bind localhost on two random high ports.
  /// Remote mode and persistence of the [XAutomate]-style keys
  /// (Mode/BindAddress/ReqRepPort/PubSubPort) via dnSpy's settings service are deferred
  /// to a later increment.
  /// </summary>
  internal sealed class SessionSettings {
    /// <summary>Inclusive lower bound of the local-mode random port range (0xC000).</summary>
    public const int MinLocalPort = 0xC000;

    /// <summary>Inclusive upper bound of the local-mode random port range (0xFFFF).</summary>
    public const int MaxLocalPort = 0xFFFF;

    /// <summary>The address to bind. Local mode always uses "localhost".</summary>
    public string BindAddress { get; }

    /// <summary>True for local mode (localhost + random ports + lockfile discovery).</summary>
    public bool IsLocal { get; }

    private SessionSettings(string bindAddress, bool isLocal) {
      BindAddress = bindAddress;
      IsLocal = isLocal;
    }

    /// <summary>Creates the default local-mode settings.</summary>
    /// <returns>A local-mode <see cref="SessionSettings"/> bound to the loopback address.</returns>
    public static SessionSettings CreateLocal() => new SessionSettings("127.0.0.1", isLocal: true);

    /// <summary>The full path of this process's session lockfile in the temp directory.</summary>
    /// <returns>e.g. <c>%TEMP%\dnspy_session.12345.lock</c>.</returns>
    public static string LockfilePath() {
      int pid = Process.GetCurrentProcess().Id;
      return Path.Combine(Path.GetTempPath(), $"dnspy_session.{pid}.lock");
    }

    /// <summary>
    /// Writes the 3-line session lockfile (reqRepPort / pubSubPort / bindAddress) so that
    /// clients scanning <c>%TEMP%\dnspy_session.*.lock</c> can discover this instance.
    /// </summary>
    /// <param name="reqRepPort">The bound REQ/REP port.</param>
    /// <param name="pubSubPort">The bound PUB/SUB port.</param>
    public void WriteLockfile(int reqRepPort, int pubSubPort) {
      string contents = string.Join("\n",
          reqRepPort.ToString(),
          pubSubPort.ToString(),
          BindAddress) + "\n";
      File.WriteAllText(LockfilePath(), contents);
    }

    /// <summary>Deletes this process's session lockfile if it exists (best effort).</summary>
    public void DeleteLockfile() {
      try {
        string path = LockfilePath();
        if (File.Exists(path)) {
          File.Delete(path);
        }
      } catch (IOException) {
        // Best effort — a stale lockfile is reaped by clients (dead PID / age).
      } catch (UnauthorizedAccessException) {
      }
    }
  }
}
