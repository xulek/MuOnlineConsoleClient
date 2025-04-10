using System.Buffers;
using System.Text;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ConnectServer;

namespace MuOnlineConsole
{
    /// <summary>
    /// Builds outgoing packets for login, character interaction, and Connect Server communication.
    /// </summary>
    public static class PacketBuilder
    {
        // --- Game Server Packets ---
        public static int BuildLoginPacket(IBufferWriter<byte> writer, string username, string password, byte[] clientVersion, byte[] clientSerial, byte[] xor3Keys)
        {
            int packetLength = LoginLongPassword.Length;
            var memory = writer.GetMemory(packetLength).Slice(0, packetLength);
            var loginPacket = new LoginLongPassword(memory);

            Span<byte> userBytes = stackalloc byte[loginPacket.Username.Length];
            Span<byte> passBytes = stackalloc byte[loginPacket.Password.Length];
            userBytes.Clear();
            passBytes.Clear();

            Encoding.ASCII.GetBytes(username, userBytes);
            Encoding.ASCII.GetBytes(password, passBytes);
            userBytes.CopyTo(loginPacket.Username);
            passBytes.CopyTo(loginPacket.Password);

            EncryptXor3(loginPacket.Username, xor3Keys);
            EncryptXor3(loginPacket.Password, xor3Keys);

            loginPacket.TickCount = (uint)Environment.TickCount;
            clientVersion.CopyTo(loginPacket.ClientVersion);
            clientSerial.CopyTo(loginPacket.ClientSerial);

            return packetLength;
        }

        public static int BuildRequestCharacterListPacket(IBufferWriter<byte> writer)
        {
            int packetSize = RequestCharacterList.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new RequestCharacterList(memory);
            packet.Language = 0; // Assuming English or default

            return packetSize;
        }

        public static int BuildSelectCharacterPacket(IBufferWriter<byte> writer, string characterName)
        {
            int packetSize = SelectCharacter.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new SelectCharacter(memory);
            packet.Name = characterName;

            return packetSize;
        }

        public static int BuildInstantMoveRequestPacket(IBufferWriter<byte> writer, byte x, byte y)
        {
            int packetSize = InstantMoveRequest.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new InstantMoveRequest(memory);
            packet.TargetX = x;
            packet.TargetY = y;
            return packetSize;
        }

        public static int BuildWalkRequestPacket(IBufferWriter<byte> writer, byte startX, byte startY, byte[] path)
        {
            if (path == null || path.Length == 0) return 0;

            int stepsDataLength = (path.Length + 1) / 2;
            int packetSize = WalkRequest.GetRequiredSize(stepsDataLength);

            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new WalkRequest(memory);

            packet.SourceX = startX;
            packet.SourceY = startY;
            packet.StepCount = (byte)path.Length;
            packet.TargetRotation = (path.Length > 0) ? path[0] : (byte)0; // Assuming first step dictates initial rotation

            var directionsSpan = packet.Directions;
            int pathIndex = 0;
            for (int i = 0; i < stepsDataLength; i++)
            {
                byte highNibble = 0x0F; // Default to invalid direction if path ends early
                byte lowNibble = 0x0F;
                if (pathIndex < path.Length) highNibble = path[pathIndex++];
                if (pathIndex < path.Length) lowNibble = path[pathIndex++];
                directionsSpan[i] = (byte)((highNibble << 4) | (lowNibble & 0x0F));
            }
            return packetSize;
        }

        /// <summary>
        /// Builds the packet to request picking up an item from the ground.
        /// </summary>
        /// <param name="writer">The buffer writer.</param>
        /// <param name="itemId">The id of the item on the ground.</param>
        /// <param name="version">The target protocol version.</param>
        /// <returns>The size of the built packet.</returns>
        public static int BuildPickupItemRequestPacket(IBufferWriter<byte> writer, ushort itemId, TargetProtocolVersion version)
        {
            // Choose the correct packet structure based on the version
            if (version == TargetProtocolVersion.Version097 || version == TargetProtocolVersion.Season6) // C3 22 is used from 0.97 onwards
            {
                int packetSize = PickupItemRequest.Length;
                var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
                var packet = new PickupItemRequest(memory);
                packet.ItemId = itemId; // Item ID is BigEndian in this version
                                        // The implicit struct constructor already sets Header Type/Code/Length
                return packetSize;
            }
            else // Version075 uses C1 22
            {
                int packetSize = PickupItemRequest075.Length;
                var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
                var packet = new PickupItemRequest075(memory);
                packet.ItemId = itemId; // Item ID is BigEndian in this version
                                        // The implicit struct constructor already sets Header Type/Code/Length
                return packetSize;
            }
        }

        public static int BuildAnimationRequestPacket(IBufferWriter<byte> writer, byte rotation, byte animationNumber)
        {
            int packetSize = AnimationRequest.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new AnimationRequest(memory);
            packet.Rotation = rotation;
            packet.AnimationNumber = animationNumber;
            return packetSize;
        }

        // --- Connect Server Packets ---

        /// <summary>
        /// Builds the packet to request the server list from the Connect Server.
        /// </summary>
        public static int BuildServerListRequestPacket(IBufferWriter<byte> writer)
        {
            // Assuming ServerListRequest exists in MUnique.OpenMU.Network.Packets.ConnectServer
            int packetSize = ServerListRequest.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            _ = new ServerListRequest(memory); // Initialize header etc.
            return packetSize;
        }

        /// <summary>
        /// Builds the packet to request connection information for a specific game server.
        /// </summary>
        public static int BuildServerInfoRequestPacket(IBufferWriter<byte> writer, ushort serverId)
        {
            // Assuming ConnectionInfoRequest exists in MUnique.OpenMU.Network.Packets.ConnectServer
            int packetSize = ConnectionInfoRequest.Length;
            var memory = writer.GetMemory(packetSize).Slice(0, packetSize);
            var packet = new ConnectionInfoRequest(memory);
            packet.ServerId = serverId;
            return packetSize;
        }


        // --- Helper Methods ---
        private static void EncryptXor3(Span<byte> data, byte[] xor3Keys)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= xor3Keys[i % 3];
            }
        }
    }
}