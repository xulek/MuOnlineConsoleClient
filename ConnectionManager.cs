using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

namespace MuOnlineConsole
{    /// <summary>
     /// Manages network connection and encryption/decryption pipeline.
     /// </summary>
    public class ConnectionManager : IAsyncDisposable
    {
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly string _host;
        private readonly int _port;
        private readonly SimpleModulusKeys _encryptKeys;
        private readonly SimpleModulusKeys _decryptKeys;
        private IDuplexPipe? _socketPipe;
        private IConnection? _connection;
        private CancellationTokenSource? _cancellationTokenSource;

        public IConnection Connection => _connection ?? throw new InvalidOperationException("Connection has not been initialized.");
        public bool IsConnected => _connection?.Connected ?? false;

        public ConnectionManager(ILoggerFactory loggerFactory, string host, int port, SimpleModulusKeys encryptKeys, SimpleModulusKeys decryptKeys)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ConnectionManager>();
            _host = host;
            _port = port;
            _encryptKeys = encryptKeys;
            _decryptKeys = decryptKeys;
        }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                _logger.LogWarning("🔌 Already connected.");
                return true;
            }

            _logger.LogInformation("🔌 Connecting to {Host}:{Port}...", _host, _port);
            try
            {
                var ipAddress = (await Dns.GetHostAddressesAsync(_host, cancellationToken))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipAddress == null)
                {
                    _logger.LogError("❓ Failed to resolve IPv4 address for host: {Host}", _host);
                    return false;
                }
                var endPoint = new IPEndPoint(ipAddress, _port);

                _socketPipe = await SocketConnection.ConnectAsync(endPoint);
                _logger.LogInformation("✔️ Socket connected.");

                var connectionLogger = _loggerFactory.CreateLogger<Connection>();

                // Set up encryption/decryption pipeline
                var decryptor = new PipelinedSimpleModulusDecryptor(_socketPipe.Input, _decryptKeys);
                var simpleModulusEncryptor = new PipelinedSimpleModulusEncryptor(_socketPipe.Output, _encryptKeys);
                var xor32Encryptor = new PipelinedXor32Encryptor(simpleModulusEncryptor.Writer);

                _connection = new Connection(_socketPipe, decryptor, xor32Encryptor, connectionLogger);
                _cancellationTokenSource = new CancellationTokenSource();

                _ = _connection.BeginReceiveAsync(); // Start background receiving
                _logger.LogInformation("👂 Started listening for packets.");
                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "❌ Socket error during connection: {ErrorCode}", ex.SocketErrorCode);
                await DisposeAsync();
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("🚫 Connection attempt cancelled (likely during DNS).");
                await DisposeAsync();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Unexpected error while connecting.");
                await DisposeAsync();
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_connection != null && _connection.Connected)
            {
                _logger.LogInformation("🔌 Disconnecting...");
                try
                {
                    await _connection.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "💥 Error during disconnect.");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();

            if (_connection != null)
            {
                if (_connection.Connected)
                {
                    await DisconnectAsync();
                }

                if (_connection is IAsyncDisposable asyncDisposableConnection)
                    await asyncDisposableConnection.DisposeAsync();
                else if (_connection is IDisposable disposableConnection)
                    disposableConnection.Dispose();
                _connection = null;
            }

            if (_socketPipe is IAsyncDisposable asyncDisposablePipe)
                await asyncDisposablePipe.DisposeAsync();
            else if (_socketPipe is IDisposable disposablePipe)
                disposablePipe.Dispose();
            _socketPipe = null;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _logger.LogInformation("🧹 ConnectionManager cleaned up.");
        }
    }
}