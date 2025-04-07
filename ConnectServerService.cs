using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ConnectServer; // Assuming CS packets are here

namespace MuOnlineConsole
{
    /// <summary>
    /// Handles communication with the Connect Server.
    /// </summary>
    public class ConnectServerService
    {
        private readonly ConnectionManager _connectionManager;
        private readonly ILogger<ConnectServerService> _logger;

        public ConnectServerService(ConnectionManager connectionManager, ILogger<ConnectServerService> logger)
        {
            _connectionManager = connectionManager;
            _logger = logger;
        }

        /// <summary>
        /// Sends a request for the server list to the Connect Server.
        /// </summary>
        public async Task RequestServerListAsync()
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection to Connect Server – cannot request server list.");
                return;
            }

            _logger.LogInformation("📜 Sending ServerListRequest packet...");
            try
            {
                // Use the appropriate packet builder method
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildServerListRequestPacket(_connectionManager.Connection.Output)
                );
                _logger.LogInformation("✔️ ServerListRequest packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending ServerListRequest packet.");
            }
        }

        /// <summary>
        /// Sends a request for connection information for a specific game server.
        /// </summary>
        /// <param name="serverId">The ID of the game server.</param>
        public async Task RequestConnectionInfoAsync(ushort serverId)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection to Connect Server – cannot request connection info.");
                return;
            }

            _logger.LogInformation("ℹ️ Requesting connection info for Server ID {ServerId}...", serverId);
            try
            {
                // Use the appropriate packet builder method
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildServerInfoRequestPacket(_connectionManager.Connection.Output, serverId)
                );
                _logger.LogInformation("✔️ ConnectionInfoRequest packet sent for Server ID {ServerId}.", serverId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending ConnectionInfoRequest packet for Server ID {ServerId}.", serverId);
            }
        }
    }
}