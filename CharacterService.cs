using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;

namespace MuOnlineConsole
{
    /// <summary>
    /// Handles character-related logic, such as listing and selecting characters.
    /// </summary>
    public class CharacterService
    {
        private readonly ConnectionManager _connectionManager;
        private readonly ILogger _logger;

        public CharacterService(ConnectionManager connectionManager, ILogger logger)
        {
            _connectionManager = connectionManager;
            _logger = logger;
        }

        public async Task RequestCharacterListAsync()
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send character list request.");
                return;
            }

            _logger.LogInformation("📜 Sending RequestCharacterList packet...");
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildRequestCharacterListPacket(_connectionManager.Connection.Output)
                );
                _logger.LogInformation("✔️ RequestCharacterList packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending RequestCharacterList packet.");
            }
        }

        public async Task SelectCharacterAsync(string characterName)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send character selection request.");
                return;
            }

            _logger.LogInformation("👤 Sending SelectCharacter packet for character '{CharacterName}'...", characterName);
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildSelectCharacterPacket(_connectionManager.Connection.Output, characterName)
                );
                _logger.LogInformation("✔️ SelectCharacter packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending SelectCharacter packet.");
            }
        }

        public async Task SendInstantMoveRequestAsync(byte x, byte y)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send move request.");
                return;
            }
            _logger.LogInformation("🏃 Sending InstantMove packet to ({X},{Y})...", x, y);
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildInstantMoveRequestPacket(_connectionManager.Connection.Output, x, y)
                );
                _logger.LogInformation("✔️ InstantMove packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending InstantMove packet.");
            }
        }

        public async Task SendAnimationRequestAsync(byte rotation, byte animationNumber)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send animation request.");
                return;
            }
            _logger.LogInformation("🔄 Sending AnimationRequest packet (Rot: {Rot}, Anim: {Anim})...", rotation, animationNumber);
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildAnimationRequestPacket(_connectionManager.Connection.Output, rotation, animationNumber)
                );
                _logger.LogInformation("✔️ AnimationRequest packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending AnimationRequest packet.");
            }
        }

        public async Task SendWalkRequestAsync(byte startX, byte startY, byte[] path)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogError("🔒 No connection – cannot send walk request.");
                return;
            }

            if (path == null || path.Length == 0)
            {
                _logger.LogWarning("🚶 Empty path – walk request not sent.");
                return;
            }

            _logger.LogInformation("🚶 Sending WalkRequest packet with start ({StartX},{StartY}), {Steps} steps...", startX, startY, path.Length);
            try
            {
                await _connectionManager.Connection.SendAsync(() =>
                    PacketBuilder.BuildWalkRequestPacket(_connectionManager.Connection.Output, startX, startY, path)
                );
                _logger.LogInformation("✔️ WalkRequest packet sent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending WalkRequest packet.");
            }
        }
    }
}