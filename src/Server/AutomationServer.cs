using System;
using NetMQ;
using NetMQ.Sockets;

namespace dnSpyAutomate.Server {
  /// <summary>
  /// Owns the ZMQ sockets and the background request loop.
  ///
  /// A single <see cref="ResponseSocket"/> (REQ/REP) handles synchronous commands one at a
  /// time via a <see cref="NetMQPoller"/> running on its own thread. A
  /// <see cref="PublisherSocket"/> (PUB/SUB) carries async debugger events, fed from other
  /// threads through a <see cref="NetMQQueue{T}"/> registered on the poller (NetMQ sockets are
  /// not thread-safe, so the queue marshals sends onto the poller thread). Ports are chosen per
  /// <see cref="SessionSettings"/> and a discovery lockfile is written on start / removed on stop.
  /// </summary>
  internal sealed class AutomationServer {
    private readonly CommandHandlers _handlers;
    private readonly SessionSettings _settings;

    private ResponseSocket? _repSocket;
    private PublisherSocket? _pubSocket;
    private NetMQQueue<byte[]>? _eventQueue;
    private NetMQPoller? _poller;
    private bool _running;

    /// <summary>Creates the server around its command handlers and settings.</summary>
    /// <param name="handlers">The command dispatch table.</param>
    /// <param name="settings">Session/binding configuration.</param>
    public AutomationServer(CommandHandlers handlers, SessionSettings settings) {
      _handlers = handlers;
      _settings = settings;
    }

    /// <summary>The bound REQ/REP port (0 until <see cref="Start"/> succeeds).</summary>
    public int ReqRepPort { get; private set; }

    /// <summary>The bound PUB/SUB port (0 until <see cref="Start"/> succeeds).</summary>
    public int PubSubPort { get; private set; }

    /// <summary>
    /// Binds the sockets, writes the discovery lockfile, and starts the background poller.
    /// Idempotent: a second call while already running is a no-op.
    /// </summary>
    public void Start() {
      if (_running) {
        return;
      }

      _repSocket = new ResponseSocket();
      _pubSocket = new PublisherSocket();
      ReqRepPort = BindRandomLocalPort(_repSocket);
      PubSubPort = BindRandomLocalPort(_pubSocket);

      _eventQueue = new NetMQQueue<byte[]>();
      _eventQueue.ReceiveReady += OnEventReady;
      _repSocket.ReceiveReady += OnReceiveReady;
      _poller = new NetMQPoller { _repSocket, _eventQueue };

      _settings.WriteLockfile(ReqRepPort, PubSubPort);
      _running = true;
      _poller.RunAsync("dnSpy-automate server");
    }

    /// <summary>
    /// Publishes an async event frame on the PUB socket. Thread-safe: the frame is enqueued and
    /// actually sent on the poller thread (see <see cref="OnEventReady"/>). No-op if not running.
    /// </summary>
    /// <param name="frame">A MessagePack-encoded [EVENT_NAME, ...fields] frame.</param>
    public void PublishEvent(byte[] frame) {
      _eventQueue?.Enqueue(frame);
    }

    /// <summary>Drains queued event frames and sends them on the PUB socket (poller thread).</summary>
    private void OnEventReady(object? sender, NetMQQueueEventArgs<byte[]> e) {
      while (e.Queue.TryDequeue(out var frame, TimeSpan.Zero)) {
        if (frame is null) {
          continue;
        }
        try {
          _pubSocket?.SendFrame(frame);
        } catch (Exception) {
          // Socket disposed during shutdown; drop.
        }
      }
    }

    /// <summary>
    /// Stops the poller, disposes the sockets, removes the lockfile, and cleans up NetMQ.
    /// Idempotent and safe to call during app shutdown.
    /// </summary>
    public void Stop() {
      if (!_running) {
        return;
      }
      _running = false;

      try { _poller?.Stop(); } catch (Exception) { /* already stopped */ }
      try { _poller?.Dispose(); } catch (Exception) { }
      try { _repSocket?.Dispose(); } catch (Exception) { }
      try { _pubSocket?.Dispose(); } catch (Exception) { }
      try { _eventQueue?.Dispose(); } catch (Exception) { }
      _poller = null;
      _repSocket = null;
      _pubSocket = null;
      _eventQueue = null;

      _settings.DeleteLockfile();
      try { NetMQConfig.Cleanup(block: false); } catch (Exception) { }
    }

    /// <summary>
    /// Handles one request/response round-trip on the poller thread. Always sends exactly
    /// one reply so the REQ/REP socket stays in a valid state, even on failure.
    /// </summary>
    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e) {
      byte[] frame;
      try {
        frame = e.Socket.ReceiveFrameBytes();
      } catch (Exception) {
        // A failed receive leaves nothing to reply to; drop it.
        return;
      }

      object? response;
      try {
        object? request = Wire.Decode(frame);
        response = _handlers.Dispatch(request);
      } catch (Exception ex) {
        response = Wire.Error(Wire.ErrInternal, ex.Message);
      }

      try {
        e.Socket.SendFrame(Wire.Encode(response));
      } catch (Exception) {
        // Socket disposed mid-flight (shutdown); nothing more we can do.
      }
    }

    /// <summary>
    /// Binds a socket to a random local port, re-rolling on conflict — mirrors the x64dbg
    /// server's local-mode behavior.
    /// </summary>
    /// <param name="socket">The socket to bind.</param>
    /// <returns>The port that was successfully bound.</returns>
    private int BindRandomLocalPort(NetMQSocket socket) {
      var rng = new Random();
      while (true) {
        int port = rng.Next(SessionSettings.MinLocalPort, SessionSettings.MaxLocalPort + 1);
        try {
          socket.Bind($"tcp://{_settings.BindAddress}:{port}");
          return port;
        } catch (NetMQException) {
          // Port in use or otherwise unbindable — draw another and retry.
        }
      }
    }
  }
}
