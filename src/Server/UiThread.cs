using System;
using dnSpy.Contracts.App;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Marshals work onto dnSpy's WPF UI/Dispatcher thread.
  ///
  /// The ZMQ server runs on a background thread, but every command that reads or mutates
  /// dnSpy state must execute on the UI thread. Handlers wrap such access in
  /// <see cref="RunOnUI{T}"/>, which blocks the caller until the UI thread returns a result.
  /// </summary>
  internal sealed class UiThread {
    private readonly IAppWindow _appWindow;

    /// <summary>Creates the helper around the app's main window.</summary>
    /// <param name="appWindow">The dnSpy app window (provides the main window + dispatcher).</param>
    public UiThread(IAppWindow appWindow) {
      _appWindow = appWindow;
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The delegate to execute on the UI thread.</param>
    /// <returns>The value produced by <paramref name="func"/>.</returns>
    /// <exception cref="UiUnavailableException">
    /// Thrown if the main window or its dispatcher is unavailable (e.g. before the app has
    /// loaded or while it is shutting down).
    /// </exception>
    public T RunOnUI<T>(Func<T> func) {
      var window = _appWindow.MainWindow;
      if (window is null) {
        throw new UiUnavailableException("Main window is not available yet.");
      }
      var dispatcher = window.Dispatcher;
      if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) {
        throw new UiUnavailableException("UI dispatcher is shutting down.");
      }
      return dispatcher.Invoke(func);
    }
  }

  /// <summary>Raised when the UI thread cannot be reached to run a command.</summary>
  internal sealed class UiUnavailableException : Exception {
    /// <summary>Creates the exception with a descriptive message.</summary>
    /// <param name="message">Why the UI thread was unavailable.</param>
    public UiUnavailableException(string message) : base(message) { }
  }
}
