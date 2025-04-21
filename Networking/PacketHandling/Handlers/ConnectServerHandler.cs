using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets.ConnectServer;
using MuOnlineConsole.Client; // For SimpleLoginClient
using MuOnlineConsole.Core.Models; // For ServerInfo
using MuOnlineConsole.Core.Utilities; // For PacketHandlerAttribute

namespace MuOnlineConsole.Networking.PacketHandling.Handlers
{
    /// <summary>
    /// Handles packets received from the Connect Server.
    /// </summary>
    public class ConnectServerHandler : IGamePacketHandler // Can reuse interface marker
    {
        private readonly ILogger<ConnectServerHandler> _logger;
        private readonly SimpleLoginClient _client; // Needed for state changes and server list storage

        public ConnectServerHandler(ILoggerFactory loggerFactory, SimpleLoginClient client)
        {
            _logger = loggerFactory.CreateLogger<ConnectServerHandler>();
            _client = client;
        }

        // Note: These handlers are registered manually in PacketRouter for now.
        // If using attributes, they would need a way to specify Connect Server context.

        public async Task HandleHelloAsync(Memory<byte> packet) // Zmień sygnaturę na async Task
        {
            _logger.LogInformation("👋 Received Hello from Connect Server.");
            // Bezpośrednie wywołanie, ponieważ HandleHelloAsync jest już 'awaitable'
            await _client.RequestServerList();
        }

        public Task HandleServerListResponseAsync(Memory<byte> packet)
        {
            _logger.LogInformation("📊 Received Server List Response.");
            try
            {
                var serverListResponse = new ServerListResponse(packet);
                var servers = new List<ServerInfo>();
                ushort serverCount = serverListResponse.ServerCount;
                _logger.LogInformation("  Server Count: {Count}", serverCount);

                for (int i = 0; i < serverCount; i++)
                {
                    var serverLoadInfo = serverListResponse[i];
                    servers.Add(new ServerInfo
                    {
                        ServerId = serverLoadInfo.ServerId,
                        LoadPercentage = serverLoadInfo.LoadPercentage
                    });
                    _logger.LogDebug("  -> Server ID: {Id}, Load: {Load}%", serverLoadInfo.ServerId, serverLoadInfo.LoadPercentage);
                }
                _client.StoreServerList(servers);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ServerListResponse packet."); }
            return Task.CompletedTask;
        }

        public Task HandleConnectionInfoResponseAsync(Memory<byte> packet)
        {
            _logger.LogInformation("🔗 Received Connection Info Response.");
            try
            {
                var connectionInfo = new ConnectionInfo(packet);
                string ipAddress = connectionInfo.IpAddress;
                ushort port = connectionInfo.Port;
                _logger.LogInformation("  -> Game Server Address: {IP}:{Port}", ipAddress, port);
                _client.SwitchToGameServer(ipAddress, port);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ConnectionInfoResponse packet."); }
            return Task.CompletedTask;
        }
    }
}