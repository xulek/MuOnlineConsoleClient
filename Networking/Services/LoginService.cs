using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;

namespace MuOnlineConsole
{
    /// <summary>
    /// Handles login-related logic and packet sending.
    /// </summary>
    public class LoginService
    {
        private readonly ConnectionManager _connectionManager;
        private readonly ILogger _logger;
        private readonly byte[] _clientVersion;
        private readonly byte[] _clientSerial;
        private readonly byte[] _xor3Keys;

        public LoginService(ConnectionManager connectionManager, ILogger logger, byte[] clientVersion, byte[] clientSerial, byte[] xor3Keys)
        {
            _connectionManager = connectionManager;
            _logger = logger;
            _clientVersion = clientVersion;
            _clientSerial = clientSerial;
            _xor3Keys = xor3Keys;
        }

        public async Task SendLoginRequestAsync(string username, string password)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send login packet.");
                return;
            }

            _logger.LogInformation("🔑 Sending login packet for user '{Username}'...", username);
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildLoginPacket(_connectionManager.Connection.Output, username, password, _clientVersion, _clientSerial, _xor3Keys)
                );
                _logger.LogInformation("✔️ Login packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending login packet.");
            }
        }
    }
}
