using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial; // Requires the Pipelines.Sockets.Unofficial NuGet package

namespace MuOnlineConsole.Networking
{
    /// <summary>
    /// Manages TCP network connections, including establishing, maintaining, and disconnecting connections.
    /// Handles encryption and decryption pipelines using SimpleModulus and Xor32 algorithms.
    /// Supports sequential connections to different endpoints (e.g., Connect Server then Game Server).
    /// Implements IAsyncDisposable for proper resource management.
    /// </summary>
    public class ConnectionManager : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ConnectionManager> _logger;
        private readonly SimpleModulusKeys _encryptKeys; // Encryption keys for SimpleModulus
        private readonly SimpleModulusKeys _decryptKeys; // Decryption keys for SimpleModulus

        private SocketConnection? _socketConnection; // Underlying socket connection, using concrete type for disposal
        private IConnection? _connection; // Abstraction for network connection, can be encrypted or raw
        private CancellationTokenSource? _receiveCts; // Cancellation token source for controlling receive loop

        /// <summary>
        /// Gets the current network connection. Throws an exception if the connection is not initialized or has been disconnected.
        /// </summary>
        public IConnection Connection => _connection ?? throw new InvalidOperationException("Connection has not been initialized or is disconnected.");

        /// <summary>
        /// Gets a value indicating whether the current network connection is established and active.
        /// </summary>
        public bool IsConnected => _connection?.Connected ?? false;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionManager"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory used to create loggers.</param>
        /// <param name="encryptKeys">The SimpleModulus keys for encryption.</param>
        /// <param name="decryptKeys">The SimpleModulus keys for decryption.</param>
        public ConnectionManager(ILoggerFactory loggerFactory, SimpleModulusKeys encryptKeys, SimpleModulusKeys decryptKeys)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ConnectionManager>();
            _encryptKeys = encryptKeys;
            _decryptKeys = decryptKeys;
        }

        /// <summary>
        /// Establishes a TCP connection to the specified host and port.
        /// Configures the packet processing pipeline, including optional encryption based on the 'useEncryption' parameter.
        /// </summary>
        /// <param name="host">The host name or IP address of the server.</param>
        /// <param name="port">The port number of the server.</param>
        /// <param name="useEncryption">True to enable encryption (SimpleModulus and Xor32), false for a raw, unencrypted connection.</param>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the connection attempt.</param>
        /// <returns>True if the connection was successfully established; otherwise, false.</returns>
        public async Task<bool> ConnectAsync(string host, int port, bool useEncryption, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                _logger.LogWarning("🔌 Already connected. Disconnect first before connecting to a new endpoint.");
                return false; // Do not attempt to connect if already connected
            }

            _logger.LogInformation("🔌 Attempting connection to {Host}:{Port} (Encryption: {UseEncryption})...", host, port, useEncryption);
            try
            {
                // Resolve host name to IP addresses and select the first IPv4 address
                var ipAddress = (await Dns.GetHostAddressesAsync(host, cancellationToken))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); // Prefer IPv4 for compatibility
                if (ipAddress == null)
                {
                    _logger.LogError("❓ Failed to resolve IPv4 address for host: {Host}", host);
                    return false; // Abort if IPv4 address cannot be resolved
                }
                var endPoint = new IPEndPoint(ipAddress, port); // Create endpoint from IP address and port

                await CleanupCurrentConnectionAsync(); // Ensure any previous connection is properly cleaned up

                _socketConnection = await SocketConnection.ConnectAsync(endPoint); // Establish socket connection
                _logger.LogInformation("✔️ Socket connected to {EndPoint}.", endPoint);

                var connectionLogger = _loggerFactory.CreateLogger<Connection>(); // Create logger for the connection class

                IDuplexPipe transportPipe = _socketConnection; // Start with raw socket pipe

                // Setup encryption pipeline if useEncryption is true
                if (useEncryption)
                {
                    // Build decryption pipeline: Socket -> SimpleModulus Decryptor
                    var decryptor = new PipelinedSimpleModulusDecryptor(transportPipe.Input, _decryptKeys);
                    // Build encryption pipeline: Socket <- Xor32 Encryptor <- SimpleModulus Encryptor
                    var simpleModulusEncryptor = new PipelinedSimpleModulusEncryptor(transportPipe.Output, _encryptKeys);
                    var xor32Encryptor = new PipelinedXor32Encryptor(simpleModulusEncryptor.Writer);
                    _connection = new Connection(transportPipe, decryptor, xor32Encryptor, connectionLogger); // Create encrypted connection
                    _logger.LogInformation("🔒 Encryption pipeline established.");
                }
                else
                {
                    // Use raw transport pipe for unencrypted connection
                    _connection = new Connection(transportPipe, null, null, connectionLogger);
                    _logger.LogInformation("🔓 Raw (unencrypted) pipeline established.");
                }

                _receiveCts = new CancellationTokenSource(); // Initialize cancellation token source for receive loop
                _ = _connection.BeginReceiveAsync(); // Start asynchronous receive loop in background
                _logger.LogInformation("👂 Started listening for packets on new connection.");
                return true; // Connection successful
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "❌ Socket error during connection to {Host}:{Port}: {ErrorCode}", host, port, ex.SocketErrorCode);
                await CleanupCurrentConnectionAsync(); // Cleanup resources on connection failure
                return false; // Connection failed due to socket error
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("🚫 Connection attempt to {Host}:{Port} cancelled.", host, port);
                await CleanupCurrentConnectionAsync(); // Cleanup resources on cancellation
                return false; // Connection cancelled
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Unexpected error while connecting to {Host}:{Port}.", host, port);
                await CleanupCurrentConnectionAsync(); // Cleanup resources on unexpected error
                return false; // Connection failed due to unexpected error
            }
        }

        /// <summary>
        /// Gracefully disconnects the current network connection.
        /// </summary>
        /// <returns>A Task representing the asynchronous disconnect operation.</returns>
        public async Task DisconnectAsync()
        {
            if (_connection != null && _connection.Connected)
            {
                _logger.LogInformation("🔌 Disconnecting current connection...");
                try
                {
                    await _connection.DisconnectAsync(); // Initiate disconnect sequence
                    _logger.LogInformation("✔️ Connection disconnected.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "💥 Error during disconnect.");
                }
            }
            else
            {
                _logger.LogInformation("🔌 No active connection to disconnect.");
            }
            await CleanupCurrentConnectionAsync(); // Ensure resources are cleaned up after disconnection
        }

        /// <summary>
        /// Cleans up resources associated with the current connection, including socket, connection object, and cancellation tokens.
        /// </summary>
        private async Task CleanupCurrentConnectionAsync()
        {
            _receiveCts?.Cancel(); // Request cancellation of receive loop

            // Dispose of IConnection, which should handle pipe completion and disposal
            if (_connection is IAsyncDisposable asyncDisposableConnection)
            {
                try { await asyncDisposableConnection.DisposeAsync(); } catch (ObjectDisposedException) { /* Expected if already disposed */ } catch (Exception ex) { _logger.LogError(ex, "Error during IAsyncDisposable connection cleanup."); }
            }
            else if (_connection is IDisposable disposableConnection)
            {
                try { disposableConnection.Dispose(); } catch (ObjectDisposedException) { /* Expected if already disposed */ } catch (Exception ex) { _logger.LogError(ex, "Error during IDisposable connection cleanup."); }
            }
            _connection = null; // Dereference connection object

            // Dispose of SocketConnection to release socket resources
            if (_socketConnection != null)
            {
                try
                {
                    _socketConnection.Dispose(); // Dispose of socket connection
                }
                catch (ObjectDisposedException) { /* Expected if already disposed */ }
                catch (Exception ex) { _logger.LogError(ex, "Error during SocketConnection cleanup."); }
                _socketConnection = null; // Dereference socket connection
            }

            // Dispose of CancellationTokenSource to release resources
            try { _receiveCts?.Dispose(); } catch (ObjectDisposedException) { /* Expected if already disposed */ }
            _receiveCts = null; // Dereference cancellation token source

            _logger.LogDebug("Connection resources cleaned up.");
        }

        /// <summary>
        /// Asynchronously disposes of the ConnectionManager, ensuring all resources are released.
        /// </summary>
        /// <returns>A ValueTask representing the completion of the disposal.</returns>
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("🧹 Cleaning up ConnectionManager...");
            await DisconnectAsync(); // Disconnect and cleanup active connection
            _logger.LogInformation("✔️ ConnectionManager cleaned up.");
            GC.SuppressFinalize(this); // Suppress finalization to prevent GC from calling finalizer after DisposeAsync
        }
    }
}