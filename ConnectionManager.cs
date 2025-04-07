using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

namespace MuOnlineConsole
{
    /// <summary>
    /// Manages network connection and encryption/decryption pipeline.
    /// Allows connecting to different endpoints sequentially.
    /// </summary>
    public class ConnectionManager : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ConnectionManager> _logger;
        private readonly SimpleModulusKeys _encryptKeys;
        private readonly SimpleModulusKeys _decryptKeys;

        private IDuplexPipe? _socketPipe;
        private IConnection? _connection;
        private CancellationTokenSource? _receiveCts;

        public IConnection Connection => _connection ?? throw new InvalidOperationException("Connection has not been initialized or is disconnected.");
        public bool IsConnected => _connection?.Connected ?? false;

        public ConnectionManager(ILoggerFactory loggerFactory, SimpleModulusKeys encryptKeys, SimpleModulusKeys decryptKeys)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ConnectionManager>();
            _encryptKeys = encryptKeys;
            _decryptKeys = decryptKeys;
        }

        /// <summary>
        /// Connects to the specified host and port.
        /// </summary>
        /// <param name="host">The host to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <param name="useEncryption">Whether to use the encryption pipeline.</param>
        /// <param name="cancellationToken">Cancellation token for the connection attempt.</param>
        /// <returns>True if connection was successful, otherwise false.</returns>
        public async Task<bool> ConnectAsync(string host, int port, bool useEncryption, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                _logger.LogWarning("🔌 Already connected. Disconnect first before connecting to a new endpoint.");
                return false; // Or should we disconnect and reconnect? Let's enforce explicit disconnect for now.
            }

            _logger.LogInformation("🔌 Connecting to {Host}:{Port} (Encryption: {UseEncryption})...", host, port, useEncryption);
            try
            {
                var ipAddress = (await Dns.GetHostAddressesAsync(host, cancellationToken))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipAddress == null)
                {
                    _logger.LogError("❓ Failed to resolve IPv4 address for host: {Host}", host);
                    return false;
                }
                var endPoint = new IPEndPoint(ipAddress, port);

                // Ensure previous resources are cleaned up if any (shouldn't happen if IsConnected is checked, but belt and suspenders)
                await CleanupCurrentConnectionAsync();

                _socketPipe = await SocketConnection.ConnectAsync(endPoint, null); // Pass null for options
                _logger.LogInformation("✔️ Socket connected to {EndPoint}.", endPoint);

                var connectionLogger = _loggerFactory.CreateLogger<Connection>();

                if (useEncryption)
                {
                    // Set up encryption/decryption pipeline
                    var decryptor = new PipelinedSimpleModulusDecryptor(_socketPipe.Input, _decryptKeys);
                    var simpleModulusEncryptor = new PipelinedSimpleModulusEncryptor(_socketPipe.Output, _encryptKeys);
                    var xor32Encryptor = new PipelinedXor32Encryptor(simpleModulusEncryptor.Writer);
                    _connection = new Connection(_socketPipe, decryptor, xor32Encryptor, connectionLogger);
                    _logger.LogInformation("🔒 Encryption pipeline established.");
                }
                else
                {
                    // *** Pass null for decryptor/encryptor for unencrypted connection ***
                    _connection = new Connection(_socketPipe, null, null, connectionLogger);
                    _logger.LogInformation("🔓 Raw (unencrypted) pipeline established.");
                }

                _receiveCts = new CancellationTokenSource();
                _ = _connection.BeginReceiveAsync(); // Start background receiving
                _logger.LogInformation("👂 Started listening for packets on new connection.");
                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "❌ Socket error during connection to {Host}:{Port}: {ErrorCode}", host, port, ex.SocketErrorCode);
                await CleanupCurrentConnectionAsync();
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("🚫 Connection attempt to {Host}:{Port} cancelled.", host, port);
                await CleanupCurrentConnectionAsync();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Unexpected error while connecting to {Host}:{Port}.", host, port);
                await CleanupCurrentConnectionAsync();
                return false;
            }
        }

        /// <summary>
        /// Disconnects the current connection.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_connection != null && _connection.Connected)
            {
                _logger.LogInformation("🔌 Disconnecting current connection...");
                try
                {
                    await _connection.DisconnectAsync();
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
            // Always cleanup resources after attempting disconnect
            await CleanupCurrentConnectionAsync();
        }

        /// <summary>
        /// Cleans up resources related to the current connection.
        /// </summary>
        private async Task CleanupCurrentConnectionAsync()
        {
            var currentCts = _receiveCts; // Capture the current CTS
            currentCts?.Cancel(); // Signal receiver loop to stop

            // Give the receive loop a moment to process the cancellation before disposing resources.
            // This is a workaround for potential race conditions if DisconnectAsync/DisposeAsync
            // don't fully guarantee the receive loop has exited.
            if (currentCts != null)
            {
                try
                {
                    // A short delay (e.g., 100ms). Adjust if necessary, but keep it minimal.
                    await Task.Delay(100, CancellationToken.None); // Use CancellationToken.None to avoid cancelling the delay itself
                }
                catch (TaskCanceledException)
                {
                    // This might happen if the application is shutting down rapidly, ignore.
                    _logger.LogDebug("Cleanup delay was cancelled.");
                }
                catch (ObjectDisposedException)
                {
                    // CTS might already be disposed if DisconnectAsync was called multiple times rapidly.
                    _logger.LogDebug("Cleanup delay CTS was already disposed.");
                }
            }

            var connectionToDispose = _connection;
            _connection = null; // Nullify immediately to prevent reuse attempts

            if (connectionToDispose != null)
            {
                // Ensure disconnect was called, even if already marked as not connected.
                // DisconnectAsync should ideally be idempotent or handle multiple calls gracefully.
                _logger.LogDebug("Ensuring connection is disposed during cleanup.");
                try
                {
                    // Disconnect again might be redundant if already called, but safer.
                    // It should also handle the pipe closing.
                    await connectionToDispose.DisconnectAsync();
                }
                catch (ObjectDisposedException) { /* Already disposed, expected */ }
                catch (Exception ex) { _logger.LogError(ex, "Error during connection cleanup disconnect/dispose."); }

                // Explicitly dispose if IAsyncDisposable/IDisposable
                if (connectionToDispose is IAsyncDisposable asyncDisposableConnection)
                {
                    try { await asyncDisposableConnection.DisposeAsync(); } catch (Exception ex) { _logger.LogError(ex, "Error during IAsyncDisposable connection cleanup."); }
                }
                else if (connectionToDispose is IDisposable disposableConnection)
                {
                    try { disposableConnection.Dispose(); } catch (Exception ex) { _logger.LogError(ex, "Error during IDisposable connection cleanup."); }
                }
            }

            // Explicitly dispose the pipe if it wasn't disposed by the connection.
            // This might be needed depending on the Connection implementation details.
            var pipeToDispose = _socketPipe;
            _socketPipe = null; // Nullify immediately
            if (pipeToDispose != null)
            {
                _logger.LogDebug("Disposing socket pipe during cleanup.");
                if (pipeToDispose is IAsyncDisposable asyncPipe)
                {
                    try { await asyncPipe.DisposeAsync(); } catch (Exception ex) { _logger.LogError(ex, "Error during IAsyncDisposable pipe cleanup."); }
                }
                else if (pipeToDispose is IDisposable syncPipe)
                {
                    try { syncPipe.Dispose(); } catch (Exception ex) { _logger.LogError(ex, "Error during IDisposable pipe cleanup."); }
                }
            }


            // Dispose the CancellationTokenSource
            try
            {
                currentCts?.Dispose();
            }
            catch (ObjectDisposedException) { /* Already disposed, expected */ }
            if (_receiveCts == currentCts) // Only nullify if it hasn't been replaced by a new connection attempt already
            {
                _receiveCts = null;
            }
        }


        /// <summary>
        /// Disposes the ConnectionManager and cleans up any active connection.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("🧹 Cleaning up ConnectionManager...");
            await DisconnectAsync(); // Ensure disconnection and resource cleanup
            _logger.LogInformation("✔️ ConnectionManager cleaned up.");
            // No other managed resources specific to ConnectionManager itself to dispose here
            // LoggerFactory is managed externally.
        }
    }
}