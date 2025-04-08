using System.Buffers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.Packets.ConnectServer;

namespace MuOnlineConsole
{
    public class PacketRouter
    {
        private const byte NoSubCode = 0xFF;

        private readonly ILogger<PacketRouter> _logger;
        private readonly CharacterService _characterService;
        private readonly SimpleLoginClient _clientState;
        public TargetProtocolVersion TargetVersion { get; }

        private readonly Dictionary<(byte MainCode, byte SubCode), Func<Memory<byte>, Task>> _packetHandlers = new();
        private bool _isConnectServerRouting = false; // Flag to determine routing context
        private readonly MuOnlineSettings _settings;

        public PacketRouter(
            ILogger<PacketRouter> logger,
            CharacterService characterService,
            LoginService loginService,
            TargetProtocolVersion targetVersion,
            SimpleLoginClient clientState,
            MuOnlineSettings settings)
        {
            _logger = logger;
            _characterService = characterService;
            TargetVersion = targetVersion;
            _clientState = clientState;
            _settings = settings;

            RegisterAttributeBasedHandlers();
            RegisterConnectServerHandlers();
        }

        /// <summary>
        /// Sets the routing mode (Connect Server or Game Server).
        /// </summary>
        /// <param name="isConnectServer">True if routing for Connect Server, false for Game Server.</param>
        public void SetRoutingMode(bool isConnectServer)
        {
            _isConnectServerRouting = isConnectServer;
            _logger.LogInformation("🔄 Packet routing mode set to: {Mode}", isConnectServer ? "Connect Server" : "Game Server");
        }

        public Task RoutePacketAsync(ReadOnlySequence<byte> sequence)
        {
            var packet = sequence.ToArray(); // Consider using SequenceReader for performance if needed
            _logger.LogDebug("📬 Received packet ({Length} bytes): {Data}", packet.Length, Convert.ToHexString(packet));

            if (_isConnectServerRouting)
            {
                return RouteConnectServerPacketAsync(packet);
            }
            else
            {
                return RouteGameServerPacketAsync(packet);
            }
        }

        private Task RouteGameServerPacketAsync(Memory<byte> packet)
        {
            if (!TryParsePacketHeader(packet.Span, out byte headerType, out byte code, out byte? subCode, out Memory<byte> packetMemory))
            {
                _logger.LogWarning("❓ Failed to parse Game Server packet header: {Data}", Convert.ToHexString(packet.Span));
                return Task.CompletedTask;
            }

            _logger.LogDebug("🔎 Parsing GS Packet: Header={HeaderType:X2}, Code={Code:X2}, SubCode={SubCode}",
                headerType, code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");

            return DispatchPacketInternalAsync(packetMemory, code, subCode, headerType);
        }

        private Task RouteConnectServerPacketAsync(Memory<byte> packet)
        {
            // Connect Server packets have a simpler structure (C1/C2 usually)
            if (packet.Length < 3)
            {
                _logger.LogWarning("❓ Connect Server packet too short: {Data}", Convert.ToHexString(packet.Span));
                return Task.CompletedTask;
            }

            byte headerType = packet.Span[0];
            byte code = 0;
            byte? subCode = null;
            bool parseSuccess = false;

            try
            {
                switch (headerType)
                {
                    case 0xC1:
                        code = packet.Span[2];
                        if (packet.Length >= 4 && ConnectServerSubCodeHolder.ContainsSubCode(code))
                        {
                            subCode = packet.Span[3];
                        }
                        parseSuccess = true;
                        break;
                    case 0xC2:
                        if (packet.Length < 4) // Need at least C2 LL LH Code
                        {
                            _logger.LogWarning("❓ Connect Server C2 packet too short for code: {Data}", Convert.ToHexString(packet.Span));
                            return Task.CompletedTask;
                        }
                        code = packet.Span[3]; // Code is at index 3 for C2 packets
                        if (packet.Length >= 5 && ConnectServerSubCodeHolder.ContainsSubCode(code))
                        {
                            subCode = packet.Span[4]; // SubCode is at index 4 for C2 packets
                        }
                        parseSuccess = true;
                        break;
                    default:
                        _logger.LogWarning("❓ Unknown Connect Server header type: {HeaderType:X2}", headerType);
                        return Task.CompletedTask;
                }
            }
            catch (IndexOutOfRangeException ex)
            {
                _logger.LogError(ex, "💥 Index error during Connect Server header parsing for packet: {Data}", Convert.ToHexString(packet.Span));
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 General error during Connect Server header parsing.");
                return Task.CompletedTask;
            }

            if (!parseSuccess)
            {
                _logger.LogWarning("❓ Failed to parse Connect Server packet header: {Data}", Convert.ToHexString(packet.Span));
                return Task.CompletedTask;
            }

            _logger.LogDebug("🔎 Parsing CS Packet: Header={HeaderType:X2}, Code={Code:X2}, SubCode={SubCode}",
                             headerType, code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");

            // Dispatch using the same mechanism, but handlers are registered differently or checked based on _isConnectServerRouting
            return DispatchPacketInternalAsync(packet, code, subCode, headerType);
        }


        private Task DispatchPacketInternalAsync(Memory<byte> packet, byte code, byte? subCode, byte headerType)
        {
            byte lookupSubCode = subCode ?? NoSubCode;

            // 🌦️ Skip weather packets if disabled in config
            if (code == 0x0F && _settings?.PacketLogging?.ShowWeather == false)
                return Task.CompletedTask;

            // 💔 Skip damage packets if disabled in config
            if (code == 0x11 && _settings?.PacketLogging?.ShowDamage == false)
                return Task.CompletedTask;

            var handlerKey = (code, lookupSubCode);

            if (_packetHandlers.TryGetValue(handlerKey, out var handler))
            {
                return ExecuteHandler(handler, packet, code, lookupSubCode);
            }

            // Fallback for packets with subcodes where only a main code handler exists
            if (lookupSubCode != NoSubCode && _packetHandlers.TryGetValue((code, NoSubCode), out var mainCodeHandler))
            {
                // Check if this fallback makes sense for the current routing mode
                // For now, let's assume it might be valid for both, but could be refined.
                return ExecuteHandler(mainCodeHandler, packet, code, NoSubCode);
            }

            LogUnhandled(code, subCode);
            return Task.CompletedTask;
        }

        private async Task ExecuteHandler(Func<Memory<byte>, Task> handler, Memory<byte> packet, byte code, byte subCode)
        {
            try
            {
                await handler(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Exception dispatching handler for {MainCode:X2}-{SubCode:X2}.", code, subCode);
                // Specific error handling for GS packets
                if (!_isConnectServerRouting && (code == 0xD4 || code == 0x15)) // ObjectMoved/Walked
                {
                    _clientState.SignalMovementHandled();
                }
            }
        }

        public Task OnDisconnected()
        {
            _logger.LogWarning("🔌 Disconnected from server.");
            _clientState.ClearScope(true); // Clear everything on disconnect
            _clientState.SetInGameStatus(false);
            // Reset state based on which server we were connected to
            if (!_isConnectServerRouting) // If disconnected from Game Server
            {
                _clientState.SetInGameStatus(false);
                _logger.LogInformation("🔌 Disconnected from Game Server. State reset.");
                // Potentially reset to initial state or allow reconnecting?
            }
            else // If disconnected from Connect Server
            {
                // Handle CS disconnection state if needed
                _logger.LogInformation("🔌 Disconnected from Connect Server.");
            }
            // Consider resetting the routing mode or client state here
            return Task.CompletedTask;
        }

        private void RegisterAttributeBasedHandlers()
        {
            // This registers Game Server handlers
            var methods = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
            int count = 0;
            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<PacketHandlerAttribute>();
                if (attr == null) continue;

                var parameters = method.GetParameters();
                if (method.ReturnType != typeof(Task) || parameters.Length != 1 || parameters[0].ParameterType != typeof(Memory<byte>))
                {
                    _logger.LogWarning("⚠️ Invalid packet handler signature for method {MethodName}. Expected 'Task MethodNameAsync(Memory<byte> packet)'. Skipping registration.", method.Name);
                    continue;
                }
                try
                {
                    var handlerDelegate = (Func<Memory<byte>, Task>)Delegate.CreateDelegate(typeof(Func<Memory<byte>, Task>), this, method);
                    var handlerKey = (attr.MainCode, attr.SubCode);
                    if (_packetHandlers.ContainsKey(handlerKey)) { _logger.LogWarning("⚠️ Duplicate packet handler registration attempted for {MainCode:X2}-{SubCode:X2}. Method {MethodName} ignored.", attr.MainCode, attr.SubCode, method.Name); }
                    else { _packetHandlers[handlerKey] = handlerDelegate; count++; }
                }
                catch (Exception ex) { _logger.LogError(ex, "💥 Failed to create delegate for handler method {MethodName}. Skipping registration.", method.Name); }
            }
            _logger.LogInformation("✅ Game Server packet handler registration complete. {Count} handlers registered.", count);
        }

        private void RegisterConnectServerHandlers()
        {
            // Manually register CS handlers here for simplicity, or use attributes with a flag/different attribute type
            // Example:
            _packetHandlers[(0x00, 0x01)] = HandleHelloAsync; // C1 00 01
            _packetHandlers[(0xF4, 0x06)] = HandleServerListResponseAsync; // C2 F4 06
            _packetHandlers[(0xF4, 0x03)] = HandleConnectionInfoResponseAsync; // C1 F4 03

            _logger.LogInformation("✅ Connect Server packet handler registration complete.");
        }

        private bool TryParsePacketHeader(ReadOnlySpan<byte> packet, out byte headerType, out byte code, out byte? subCode, out Memory<byte> packetMemory)
        {
            // Keep original logic for GS packets
            headerType = 0; code = 0; subCode = null; packetMemory = packet.ToArray(); // Inefficient, but keeps signature. Consider refactoring later.
            if (packet.Length < 3) return false;
            headerType = packet[0];
            try
            {
                switch (headerType)
                {
                    case 0xC1:
                    case 0xC3:
                        code = packet[2];
                        subCode = packet.Length >= 4 && SubCodeHolder.ContainsSubCode(code) ? packet[3] : (byte?)null;
                        break;
                    case 0xC2:
                    case 0xC4:
                        if (packet.Length < 4) return false;
                        code = packet[3]; // Code is at index 3 for C2/C4 Game Server packets
                        subCode = packet.Length >= 5 && SubCodeHolder.ContainsSubCode(code) ? packet[4] : (byte?)null; // SubCode is at index 4
                        break;
                    default: _logger.LogWarning("❓ Unknown header type: {HeaderType:X2}", headerType); return false;
                }
                return true;
            }
            catch (IndexOutOfRangeException ex) { _logger.LogError(ex, "💥 Index error during header parsing for packet: {Data}", Convert.ToHexString(packet)); return false; }
            catch (Exception ex) { _logger.LogError(ex, "💥 General error during header parsing."); return false; }
        }

        private void LogUnhandled(byte code, byte? subCode)
        {
            _logger.LogWarning("⚠️ Unhandled packet ({Mode}): Code={Code:X2} SubCode={SubCode}",
                _isConnectServerRouting ? "CS" : "GS",
                code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");
        }

        // ==================================================
        //  Connect Server Packet Handlers
        // ==================================================
        private Task HandleHelloAsync(Memory<byte> packet)
        {
            _logger.LogInformation("👋 Received Hello from Connect Server.");
            // Automatically trigger server list request after receiving Hello
            _ = Task.Run(() => _clientState.RequestServerList());
            return Task.CompletedTask;
        }

        private Task HandleServerListResponseAsync(Memory<byte> packet)
        {
            _logger.LogInformation("📊 Received Server List Response.");
            try
            {
                var serverList = new ServerListResponse(packet);
                var servers = new List<ServerInfo>();
                ushort serverCount = serverList.ServerCount; // Assuming ServerListResponse has ServerCount
                _logger.LogInformation("  Server Count: {Count}", serverCount);

                for (int i = 0; i < serverCount; i++)
                {
                    // Accessing the struct array - Assuming ServerLoadInfo struct exists
                    var serverLoadInfo = serverList[i];
                    var serverInfo = new ServerInfo
                    {
                        ServerId = serverLoadInfo.ServerId,
                        LoadPercentage = serverLoadInfo.LoadPercentage
                    };
                    servers.Add(serverInfo);
                    _logger.LogInformation("  -> Server ID: {Id}, Load: {Load}%", serverInfo.ServerId, serverInfo.LoadPercentage);
                }
                _clientState.StoreServerList(servers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ServerListResponse packet.");
            }
            return Task.CompletedTask;
        }

        private Task HandleConnectionInfoResponseAsync(Memory<byte> packet)
        {
            _logger.LogInformation("🔗 Received Connection Info Response.");
            try
            {
                var connectionInfo = new ConnectionInfo(packet);
                string ipAddress = connectionInfo.IpAddress;
                ushort port = connectionInfo.Port;
                _logger.LogInformation("  -> Game Server Address: {IP}:{Port}", ipAddress, port);

                // Trigger the switch to the game server
                _clientState.SwitchToGameServer(ipAddress, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ConnectionInfoResponse packet.");
            }
            return Task.CompletedTask;
        }


        // ==================================================
        //  Game Server Packet Handlers
        // ==================================================

#pragma warning disable IDE0051 // Disable warning for unused private members, as they are used by reflection via PacketHandlerAttribute

        [PacketHandler(0xF1, 0x00)] // Note: This code might conflict with CS Hello if not routed correctly
        private Task HandleGameServerEnteredAsync(Memory<byte> packet)
        {
            _logger.LogInformation("➡️🚪 Received GameServerEntered (F1, 00). Requesting Login...");
            // This is now the point to send the actual login request to the Game Server
            _clientState.SendLoginRequest();
            return Task.CompletedTask;
        }

        [PacketHandler(0xF1, 0x01)]
        private Task HandleLoginResponseAsync(Memory<byte> packet)
        {
            try
            {
                var response = new LoginResponse(packet);
                _logger.LogInformation("🔑 Received LoginResponse: Result={Result} ({ResultByte:X2})", response.Success, (byte)response.Success);
                if (response.Success == LoginResponse.LoginResult.Okay)
                {
                    _logger.LogInformation("✅ Login successful! Requesting character list...");
                    Task.Run(() => _characterService.RequestCharacterListAsync());
                }
                else { _logger.LogWarning("❌ Login failed: {Reason}", response.Success); }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing LoginResponse."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x00)]
        private Task HandleCharacterListAsync(Memory<byte> packet)
        {
            try
            {
                List<string> characterNames = new();
                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        var charListS6 = new CharacterList(packet);
                        _logger.LogInformation("📜 Received character list (S6): {Count} characters.", charListS6.CharacterCount);
                        for (int i = 0; i < charListS6.CharacterCount; ++i)
                        {
                            var c = charListS6[i];
                            _logger.LogInformation("  -> Slot {Slot}: {Name} (Level {Level})", c.SlotIndex, c.Name, c.Level);
                            characterNames.Add(c.Name);
                        }
                        break;
                    case TargetProtocolVersion.Version097:
                        var charList097 = new CharacterList095(packet);
                        _logger.LogInformation("📜 Received character list (0.97): {Count} characters.", charList097.CharacterCount);
                        for (int i = 0; i < charList097.CharacterCount; ++i)
                        {
                            var c = charList097[i];
                            _logger.LogInformation("  -> Slot {Slot}: {Name} (Level {Level})", c.SlotIndex, c.Name, c.Level);
                            characterNames.Add(c.Name);
                        }
                        break;
                    case TargetProtocolVersion.Version075:
                        var charList075 = new CharacterList075(packet);
                        _logger.LogInformation("📜 Received character list (0.75): {Count} characters.", charList075.CharacterCount);
                        for (int i = 0; i < charList075.CharacterCount; ++i)
                        {
                            var c = charList075[i];
                            _logger.LogInformation("  -> Slot {Slot}: {Name} (Level {Level})", c.SlotIndex, c.Name, c.Level);
                            characterNames.Add(c.Name);
                        }
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for CharacterList.", TargetVersion);
                        return Task.CompletedTask;
                }
                if (characterNames.Count > 0)
                {
                    Task.Run(() => _clientState.SelectCharacterInteractivelyAsync(characterNames));
                }
                else { _logger.LogWarning("👤 No characters found on the account."); }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CharacterList packet."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x03)]
        private Task HandleCharacterInformationAsync(Memory<byte> packet)
        {
            try
            {
                string version = "Unknown";
                ushort mapId = 0;
                byte x = 0, y = 0;
                uint initialHp = 0, maxHp = 1, initialSd = 0, maxSd = 0;
                uint initialMana = 0, maxMana = 1, initialAg = 0, maxAg = 0;
                ushort str = 0, agi = 0, vit = 0, ene = 0, cmd = 0;

                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        if (packet.Length >= CharacterInformationExtended.Length)
                        {
                            var info = new CharacterInformationExtended(packet);
                            version = "S6 Extended";
                            mapId = info.MapId;
                            x = info.X;
                            y = info.Y;
                            initialHp = info.CurrentHealth;
                            maxHp = info.MaximumHealth;
                            initialSd = info.CurrentShield;
                            maxSd = info.MaximumShield;
                            initialMana = info.CurrentMana;
                            maxMana = info.MaximumMana;
                            initialAg = info.CurrentAbility;
                            maxAg = info.MaximumAbility;
                            str = info.Strength;
                            agi = info.Agility;
                            vit = info.Vitality;
                            ene = info.Energy;
                            cmd = info.Leadership;
                        }
                        else if (packet.Length >= CharacterInformation.Length)
                        {
                            var info = new CharacterInformation(packet);
                            version = "S6 Standard";
                            mapId = info.MapId;
                            x = info.X;
                            y = info.Y;
                            initialHp = info.CurrentHealth;
                            maxHp = info.MaximumHealth;
                            initialSd = info.CurrentShield;
                            maxSd = info.MaximumShield;
                            initialMana = info.CurrentMana;
                            maxMana = info.MaximumMana;
                            initialAg = info.CurrentAbility;
                            maxAg = info.MaximumAbility;
                            str = info.Strength;
                            agi = info.Agility;
                            vit = info.Vitality;
                            ene = info.Energy;
                            cmd = info.Leadership;
                        }
                        else
                        {
                            goto default;
                        }
                        break;
                    case TargetProtocolVersion.Version097:
                        if (packet.Length >= CharacterInformation097.Length)
                        {
                            var info = new CharacterInformation097(packet);
                            version = "0.97";
                            mapId = info.MapId;
                            x = info.X;
                            y = info.Y;
                            initialHp = info.CurrentHealth;
                            maxHp = info.MaximumHealth;
                            initialSd = 0;
                            maxSd = 0;
                            initialMana = info.CurrentMana;
                            maxMana = info.MaximumMana;
                            initialAg = info.CurrentAbility;
                            maxAg = info.MaximumAbility;
                            str = info.Strength;
                            agi = info.Agility;
                            vit = info.Vitality;
                            ene = info.Energy;
                            cmd = info.Leadership;
                        }
                        else
                        {
                            goto default;
                        }
                        break;
                    case TargetProtocolVersion.Version075:
                        if (packet.Length >= CharacterInformation075.Length)
                        {
                            var info = new CharacterInformation075(packet);
                            version = "0.75";
                            mapId = info.MapId;
                            x = info.X;
                            y = info.Y;
                            initialHp = info.CurrentHealth;
                            maxHp = info.MaximumHealth;
                            initialSd = 0;
                            maxSd = 0;
                            initialMana = info.CurrentMana;
                            maxMana = info.MaximumMana;
                            initialAg = 0;
                            maxAg = 0;
                            str = info.Strength;
                            agi = info.Agility;
                            vit = info.Vitality;
                            ene = info.Energy;
                            cmd = 0;
                        }
                        else
                        {
                            goto default;
                        }
                        break;
                    default:
                        _logger.LogWarning("⚠️ Unexpected length ({Length}) or unsupported version ({Version}) for CharacterInformation.", packet.Length, TargetVersion);
                        return Task.CompletedTask;
                }
                _logger.LogInformation("✔️ Character selected ({Version}): Map {MapId} ({X},{Y})", version, mapId, x, y);
                _logger.LogInformation("✅ Successfully entered the game world.");
                _clientState.UpdateStats(str, agi, vit, ene, cmd);
                _clientState.UpdateMaximumHealthShield(maxHp, maxSd);
                _clientState.UpdateMaximumManaAbility(maxMana, maxAg);
                _clientState.UpdateCurrentHealthShield(initialHp, initialSd);
                _clientState.UpdateCurrentManaAbility(initialMana, initialAg);
                _clientState.SetPosition(x, y);
                _clientState.SetInGameStatus(true);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CharacterInformation packet."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x10)]
        private Task HandleCharacterInventoryAsync(Memory<byte> packet)
        {
            try
            {
                if (TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var inventory = new CharacterInventory(packet);
                    _logger.LogInformation("🎒 Received CharacterInventory: {Count} items.", inventory.ItemCount);
                }
                else
                {
                    _logger.LogWarning("⚠️ CharacterInventory handling for version {Version} is not implemented.", TargetVersion);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CharacterInventory."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x05)]
        private Task HandleCharacterLevelUpdateAsync(Memory<byte> packet)
        {
            try
            {
                uint maxHp = 0, maxSd = 0, maxMana = 0, maxAg = 0;

                if (packet.Length >= CharacterLevelUpdateExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var update = new CharacterLevelUpdateExtended(packet);
                    maxHp = update.MaximumHealth;
                    maxSd = update.MaximumShield;
                    maxMana = update.MaximumMana;
                    maxAg = update.MaximumAbility;
                    _logger.LogInformation("⬆️ Received CharacterLevelUpdate (Extended): Lvl={Lvl}, Pts={Pts}", update.Level, update.LevelUpPoints);
                }
                else if (packet.Length >= CharacterLevelUpdate.Length)
                {
                    var update = new CharacterLevelUpdate(packet);
                    maxHp = update.MaximumHealth;
                    maxSd = update.MaximumShield;
                    maxMana = update.MaximumMana;
                    maxAg = update.MaximumAbility;
                    _logger.LogInformation("⬆️ Received CharacterLevelUpdate (Standard): Lvl={Lvl}, Pts={Pts}", update.Level, update.LevelUpPoints);
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for CharacterLevelUpdate packet (F3, 05).", packet.Length);
                    return Task.CompletedTask;
                }

                _clientState.UpdateMaximumHealthShield(maxHp, maxSd);
                _clientState.UpdateMaximumManaAbility(maxMana, maxAg);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CharacterLevelUpdate (F3, 05)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x06)]
        private Task HandleCharacterStatIncreaseResponseAsync(Memory<byte> packet)
        {
            try
            {
                uint maxHp = 0, maxSd = 0, maxMana = 0, maxAg = 0;
                CharacterStatAttribute attribute = default;
                bool success = false;

                if (packet.Length >= CharacterStatIncreaseResponseExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var response = new CharacterStatIncreaseResponseExtended(packet);
                    success = true;
                    attribute = response.Attribute;
                    maxHp = response.UpdatedMaximumHealth;
                    maxSd = response.UpdatedMaximumShield;
                    maxMana = response.UpdatedMaximumMana;
                    maxAg = response.UpdatedMaximumAbility;
                    _logger.LogInformation("➕ Received StatIncreaseResponse (Extended): Attribute={Attr}", attribute);
                }
                else if (packet.Length >= CharacterStatIncreaseResponse.Length)
                {
                    var response = new CharacterStatIncreaseResponse(packet);
                    success = response.Success;
                    attribute = response.Attribute;
                    switch (attribute)
                    {
                        case CharacterStatAttribute.Vitality:
                            maxHp = response.UpdatedDependentMaximumStat;
                            break;
                        case CharacterStatAttribute.Energy:
                            maxMana = response.UpdatedDependentMaximumStat;
                            break;
                    }
                    maxSd = response.UpdatedMaximumShield;
                    maxAg = response.UpdatedMaximumAbility;
                    _logger.LogInformation("➕ Received StatIncreaseResponse (Standard): Attribute={Attr}, Success={Success}", attribute, success);
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for CharacterStatIncreaseResponse packet (F3, 06).", packet.Length);
                    return Task.CompletedTask;
                }

                if (success)
                {
                    if (maxHp > 0) _clientState.UpdateMaximumHealthShield(maxHp, maxSd);
                    if (maxMana > 0) _clientState.UpdateMaximumManaAbility(maxMana, maxAg);
                    _logger.LogInformation("   -> Statistic update succeeded for {Attr}.", attribute);
                }
                else { _logger.LogWarning("   -> Statistic update failed for {Attr}.", attribute); }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CharacterStatIncreaseResponse (F3, 06)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x11)]
        private Task HandleSkillListUpdateAsync(Memory<byte> packet)
        {
            try
            {
                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        var updateS6 = new SkillListUpdate(packet);
                        if (updateS6.Count == 0xFE)
                        {
                            var added = new SkillAdded(packet);
                            _logger.LogInformation("✨ Added Skill (S6): Index={Index}, Num={Num}, Lvl={Lvl}", added.SkillIndex, added.SkillNumber, added.SkillLevel);
                        }
                        else if (updateS6.Count == 0xFF)
                        {
                            var removed = new SkillRemoved(packet);
                            _logger.LogInformation("🗑️ Removed Skill (S6): Index={Index}, Num={Num}", removed.SkillIndex, removed.SkillNumber);
                        }
                        else
                        {
                            _logger.LogInformation("✨ Received SkillListUpdate (S6): {Count} skills.", updateS6.Count);
                        }
                        break;
                    case TargetProtocolVersion.Version097:
                    case TargetProtocolVersion.Version075:
                        var updateLegacy = new SkillListUpdate075(packet);
                        if (updateLegacy.Count == 0xFE || updateLegacy.Count == 1)
                        {
                            if (TargetVersion == TargetProtocolVersion.Version075)
                            {
                                var added = new SkillAdded075(packet);
                                _logger.LogInformation("✨ Added Skill ({Version}): Index={Index}, NumLvl={NumLvl}", TargetVersion, added.SkillIndex, added.SkillNumberAndLevel);
                            }
                            else
                            {
                                var added = new SkillAdded095(packet);
                                _logger.LogInformation("✨ Added Skill ({Version}): Index={Index}, NumLvl={NumLvl}", TargetVersion, added.SkillIndex, added.SkillNumberAndLevel);
                            }
                        }
                        else if (updateLegacy.Count == 0xFF || updateLegacy.Count == 0)
                        {
                            if (TargetVersion == TargetProtocolVersion.Version075)
                            {
                                var removed = new SkillRemoved075(packet);
                                _logger.LogInformation("🗑️ Removed Skill ({Version}): Index={Index}, NumLvl={NumLvl}", TargetVersion, removed.SkillIndex, removed.SkillNumberAndLevel);
                            }
                            else
                            {
                                var removed = new SkillRemoved095(packet);
                                _logger.LogInformation("🗑️ Removed Skill ({Version}): Index={Index}, NumLvl={NumLvl}", TargetVersion, removed.SkillIndex, removed.SkillNumberAndLevel);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("✨ Received SkillListUpdate ({Version}): {Count} skills.", TargetVersion, updateLegacy.Count);
                        }
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for SkillListUpdate.", TargetVersion);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing SkillListUpdate/Add/Remove (F3, 11)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF3, 0x50)]
        private Task HandleMasterStatsUpdateAsync(Memory<byte> packet)
        {
            try
            {
                uint maxHp = 0, maxSd = 0, maxMana = 0, maxAg = 0;

                if (packet.Length >= MasterStatsUpdateExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var update = new MasterStatsUpdateExtended(packet);
                    maxHp = update.MaximumHealth;
                    maxSd = update.MaximumShield;
                    maxMana = update.MaximumMana;
                    maxAg = update.MaximumAbility;
                    _logger.LogInformation("Ⓜ️ Received MasterStatsUpdate (Extended): MasterLvl={Lvl}, MasterPts={Pts}", update.MasterLevel, update.MasterLevelUpPoints);
                }
                else if (packet.Length >= MasterStatsUpdate.Length)
                {
                    var update = new MasterStatsUpdate(packet);
                    maxHp = update.MaximumHealth;
                    maxSd = update.MaximumShield;
                    maxMana = update.MaximumMana;
                    maxAg = update.MaximumAbility;
                    _logger.LogInformation("Ⓜ️ Received MasterStatsUpdate (Standard): MasterLvl={Lvl}, MasterPts={Pts}", update.MasterLevel, update.MasterLevelUpPoints);
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for MasterStatsUpdate packet (F3, 50).", packet.Length);
                    return Task.CompletedTask;
                }

                _clientState.UpdateMaximumHealthShield(maxHp, maxSd);
                _clientState.UpdateMaximumManaAbility(maxMana, maxAg);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MasterStatsUpdate (F3, 50)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xA0, NoSubCode)]
        private Task HandleLegacyQuestStateListAsync(Memory<byte> packet)
        {
            try
            {
                var questList = new LegacyQuestStateList(packet);
                _logger.LogInformation("📜 Received LegacyQuestStateList: {Count} quests.", questList.QuestCount);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing LegacyQuestStateList (A0)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xF6, 0x1A)]
        private Task HandleQuestStateListAsync(Memory<byte> packet)
        {
            try
            {
                var stateList = new QuestStateList(packet);
                _logger.LogInformation("❓ Received QuestStateList: {Count} active/completed quests.", stateList.QuestCount);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing QuestStateList (F6, 1A)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x0D, NoSubCode)]
        private Task HandleServerMessageAsync(Memory<byte> packet)
        {
            try
            {
                var message = new ServerMessage(packet);
                _logger.LogInformation("💬 Received ServerMessage: Type={Type}, Content='{Message}'", message.Type, message.Message);
                string prefix = message.Type switch
                {
                    ServerMessage.MessageType.GoldenCenter => "[GOLDEN]: ",
                    ServerMessage.MessageType.BlueNormal => "[SYSTEM]: ",
                    ServerMessage.MessageType.GuildNotice => "[GUILD]: ",
                    _ => "[SERVER]: "
                };
                Console.WriteLine($"{prefix}{message.Message}");
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ServerMessage (0D)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x00, NoSubCode)]
        private Task HandleChatMessageAsync(Memory<byte> packet)
        {
            try
            {
                var message = new ChatMessage(packet);
                _logger.LogInformation("💬 Received ChatMessage: From={Sender}, Type={Type}, Content='{Message}'", message.Sender, message.Type, message.Message);
                string prefix = message.Type switch
                {
                    ChatMessage.ChatMessageType.Whisper => $"귓속말 [{message.Sender}]: ",
                    ChatMessage.ChatMessageType.Normal => $"[{message.Sender}]: ",
                    _ => $"[{message.Sender} ({message.Type})]: "
                };
                Console.WriteLine($"{prefix}{message.Message}");
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ChatMessage (00)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x1C, 0x0F)]
        private Task HandleMapChangedAsync(Memory<byte> packet)
        {
            try
            {
                byte posX = 0, posY = 0;
                ushort mapId = 0xFFFF; // Use ushort for S6 compatibility
                bool isActualMapChange = false;

                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        var mapChangeS6 = new MapChanged(packet);
                        mapId = mapChangeS6.MapNumber;
                        posX = mapChangeS6.PositionX;
                        posY = mapChangeS6.PositionY;
                        isActualMapChange = mapChangeS6.IsMapChange;
                        _logger.LogInformation("🗺️ Received MapChanged (S6): MapNumber={MapNumber}, Pos=({X},{Y}), IsMapChange={IsChange}", mapChangeS6.MapNumber, posX, posY, isActualMapChange);
                        break;
                    case TargetProtocolVersion.Version097:
                    case TargetProtocolVersion.Version075:
                        var mapChangeLegacy = new MapChanged075(packet);
                        mapId = mapChangeLegacy.MapNumber; // Implicit conversion might work, or cast if needed
                        posX = mapChangeLegacy.PositionX;
                        posY = mapChangeLegacy.PositionY;
                        isActualMapChange = mapChangeLegacy.IsMapChange;
                        _logger.LogInformation("🗺️ Received MapChanged ({Version}): MapNumber={MapNumber}, Pos=({X},{Y}), IsMapChange={IsChange}", TargetVersion, mapId, posX, posY, isActualMapChange);
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for MapChanged.", TargetVersion);
                        break;
                }

                if (isActualMapChange) // Only clear scope on actual map changes, not teleports within map
                {
                    _clientState.ClearScope(false); // Clear others
                }

                if (posX != 0 || posY != 0) // Update position regardless
                {
                    _clientState.SetPosition(posX, posY);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MapChanged (1C, 0F)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xC0, NoSubCode)]
        private Task HandleMessengerInitializationAsync(Memory<byte> packet)
        {
            try
            {
                var init = new MessengerInitialization(packet);
                _logger.LogInformation("✉️ Received MessengerInitialization: Letters={Letters}/{MaxLetters}, Friends={Friends}", init.LetterCount, init.MaximumLetterCount, init.FriendCount);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MessengerInitialization (C0)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x26, 0xFF)]
        private Task HandleCurrentHealthShieldAsync(Memory<byte> packet)
        {
            try
            {
                uint currentHp = 0, currentSd = 0;
                if (packet.Length >= CurrentStatsExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var stats = new CurrentStatsExtended(packet);
                    currentHp = stats.Health;
                    currentSd = stats.Shield;
                    _logger.LogDebug("💧 Parsing CurrentStats (Extended)");
                    _clientState.UpdateCurrentManaAbility(stats.Mana, stats.Ability);
                }
                else if (packet.Length >= CurrentHealthAndShield.Length)
                {
                    var stats = new CurrentHealthAndShield(packet);
                    currentHp = stats.Health;
                    currentSd = stats.Shield;
                    _logger.LogDebug("💧 Parsing CurrentHealthAndShield (Standard)");
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for CurrentHealthShield packet (26, FF).", packet.Length);
                    return Task.CompletedTask;
                }
                _clientState.UpdateCurrentHealthShield(currentHp, currentSd);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CurrentHealthShield (26, FF)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x26, 0xFE)]
        private Task HandleMaximumHealthShieldAsync(Memory<byte> packet)
        {
            try
            {
                uint maxHp = 0, maxSd = 0;
                if (packet.Length >= MaximumStatsExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var stats = new MaximumStatsExtended(packet);
                    maxHp = stats.Health;
                    maxSd = stats.Shield;
                    _logger.LogDebug("💧 Parsing MaximumStats (Extended)");
                    _clientState.UpdateMaximumManaAbility(stats.Mana, stats.Ability);
                }
                else if (packet.Length >= MaximumHealthAndShield.Length)
                {
                    var stats = new MaximumHealthAndShield(packet);
                    maxHp = stats.Health;
                    maxSd = stats.Shield;
                    _logger.LogDebug("💧 Parsing MaximumHealthAndShield (Standard)");
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for MaximumHealthShield packet (26, FE).", packet.Length);
                    return Task.CompletedTask;
                }
                _clientState.UpdateMaximumHealthShield(maxHp, maxSd);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MaximumHealthShield (26, FE)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x26, 0xFD)]
        private Task HandleItemConsumptionFailedAsync(Memory<byte> packet)
        {
            try
            {
                uint currentHp = 0, currentSd = 0;
                if (packet.Length >= ItemConsumptionFailedExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var stats = new ItemConsumptionFailedExtended(packet);
                    currentHp = stats.Health;
                    currentSd = stats.Shield;
                    _logger.LogDebug("❗ Parsing ItemConsumptionFailed (Extended)");
                }
                else if (packet.Length >= ItemConsumptionFailed.Length)
                {
                    var stats = new ItemConsumptionFailed(packet);
                    currentHp = stats.Health;
                    currentSd = stats.Shield;
                    _logger.LogDebug("❗ Parsing ItemConsumptionFailed (Standard)");
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for ItemConsumptionFailed packet (26, FD).", packet.Length);
                    return Task.CompletedTask;
                }
                _logger.LogWarning("❗ Item consumption failed. Current HP: {HP}, SD: {SD}", currentHp, currentSd);
                _clientState.UpdateCurrentHealthShield(currentHp, currentSd);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ItemConsumptionFailed (26, FD)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x27, 0xFF)]
        private Task HandleCurrentManaAbilityAsync(Memory<byte> packet)
        {
            try
            {
                uint currentMana = 0, currentAbility = 0;
                if (packet.Length >= CurrentStatsExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var stats = new CurrentStatsExtended(packet);
                    currentMana = stats.Mana;
                    currentAbility = stats.Ability;
                    _logger.LogDebug("💧 Parsing CurrentStats (Extended)");
                    _clientState.UpdateCurrentHealthShield(stats.Health, stats.Shield);
                }
                else if (packet.Length >= CurrentManaAndAbility.Length)
                {
                    var stats = new CurrentManaAndAbility(packet);
                    currentMana = stats.Mana;
                    currentAbility = stats.Ability;
                    _logger.LogDebug("💧 Parsing CurrentManaAndAbility (Standard)");
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for CurrentManaAndAbility packet (27, FF).", packet.Length);
                    return Task.CompletedTask;
                }
                _clientState.UpdateCurrentManaAbility(currentMana, currentAbility);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing CurrentManaAndAbility (27, FF)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x27, 0xFE)]
        private Task HandleMaximumManaAbilityAsync(Memory<byte> packet)
        {
            try
            {
                uint maxMana = 0, maxAbility = 0;
                if (packet.Length >= MaximumStatsExtended.Length && TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var stats = new MaximumStatsExtended(packet);
                    maxMana = stats.Mana;
                    maxAbility = stats.Ability;
                    _logger.LogDebug("💧 Parsing MaximumStats (Extended)");
                    _clientState.UpdateMaximumHealthShield(stats.Health, stats.Shield);
                }
                else if (packet.Length >= MaximumManaAndAbility.Length)
                {
                    var stats = new MaximumManaAndAbility(packet);
                    maxMana = stats.Mana;
                    maxAbility = stats.Ability;
                    _logger.LogDebug("💧 Parsing MaximumManaAndAbility (Standard)");
                }
                else
                {
                    _logger.LogWarning("⚠️ Unexpected length ({Length}) for MaximumManaAndAbility packet (27, FE).", packet.Length);
                    return Task.CompletedTask;
                }
                _clientState.UpdateMaximumManaAbility(maxMana, maxAbility);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MaximumManaAndAbility (27, FE)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x0F, NoSubCode)]
        private Task HandleWeatherStatusUpdateAsync(Memory<byte> packet)
        {
            try
            {
                var weather = new WeatherStatusUpdate(packet);
                _logger.LogInformation("☀️ Received WeatherStatusUpdate: Weather={Weather}, Variation={Variation}", weather.Weather, weather.Variation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing WeatherStatusUpdate (0F).");
            }

            return Task.CompletedTask;
        }


        [PacketHandler(0x0B, NoSubCode)]
        private Task HandleMapEventStateAsync(Memory<byte> packet)
        {
            try
            {
                var eventState = new MapEventState(packet);
                _logger.LogInformation("🎉 Received MapEventState: Event={Event}, Enabled={Enabled}", eventState.Event, eventState.Enable);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MapEventState (0B)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x12, NoSubCode)]
        private Task HandleAddCharacterToScopeAsync(Memory<byte> packet)
        {
            try
            {
                string characterNameToFind = _clientState.GetCharacterName();
                ushort foundCharacterId = 0xFFFF;

                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        ReadOnlySpan<byte> packetSpanS6 = packet.Span;
                        if (packetSpanS6.Length < 5)
                        {
                            _logger.LogWarning("AddCharactersToScope (S6) packet too short (Length: {Length})", packetSpanS6.Length);
                            return Task.CompletedTask;
                        }
                        byte countS6 = packetSpanS6[4];
                        _logger.LogInformation("👀 Received AddCharactersToScope (S6): {Count} characters.", countS6);
                        int currentOffset = 5;

                        for (int i = 0; i < countS6; i++)
                        {
                            const int baseCharacterDataSizeS6 = 36;
                            if (currentOffset + baseCharacterDataSizeS6 > packetSpanS6.Length)
                            {
                                _logger.LogWarning("Insufficient data for base character {Index} in AddCharactersToScope (S6). Offset: {Offset}, Length: {Length}", i, currentOffset, packetSpanS6.Length);
                                break;
                            }
                            ReadOnlySpan<byte> baseCharReadOnlySpan = packetSpanS6.Slice(currentOffset, baseCharacterDataSizeS6);
                            ushort id = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(baseCharReadOnlySpan.Slice(0, 2));
                            byte x = baseCharReadOnlySpan[2];
                            byte y = baseCharReadOnlySpan[3];
                            string name = packet.Span.Slice(currentOffset + 22, 10).ExtractString(0, 10, Encoding.UTF8);
                            byte effectCount = baseCharReadOnlySpan[baseCharacterDataSizeS6 - 1];
                            int fullCharacterSize = baseCharacterDataSizeS6 + effectCount;

                            if (currentOffset + fullCharacterSize > packetSpanS6.Length)
                            {
                                _logger.LogWarning("Insufficient data for full character {Index} (with {EffectCount} effects) in AddCharactersToScope (S6). Offset: {Offset}, Required: {Required}, Length: {Length}", i, effectCount, currentOffset, fullCharacterSize, packetSpanS6.Length);
                                break;
                            }

                            _clientState.AddOrUpdatePlayerInScope(id, x, y, name);

                            _logger.LogDebug("  -> Character in scope (S6): Index={Index}, ID={Id:X4}, Name='{Name}', Effects={EffectCount}, Size={Size}", i, id, name, effectCount, fullCharacterSize);
                            if (name == characterNameToFind)
                            {
                                foundCharacterId = id;
                            }
                            currentOffset += fullCharacterSize;
                        }
                        break;
                    case TargetProtocolVersion.Version097:
                        var scope097 = new AddCharactersToScope095(packet);
                        _logger.LogInformation("👀 Received AddCharactersToScope (0.97): {Count} characters.", scope097.CharacterCount);
                        for (int i = 0; i < scope097.CharacterCount; i++)
                        {
                            var c = scope097[i];
                            _clientState.AddOrUpdatePlayerInScope(c.Id, c.CurrentPositionX, c.CurrentPositionY, c.Name);
                            _logger.LogDebug("  -> Character in scope (0.97): ID={Id:X4}, Name='{Name}'", c.Id, c.Name);
                            if (c.Name == characterNameToFind) foundCharacterId = c.Id;
                        }
                        break;
                    case TargetProtocolVersion.Version075:
                        var scope075 = new AddCharactersToScope075(packet);
                        _logger.LogInformation("👀 Received AddCharactersToScope (0.75): {Count} characters.", scope075.CharacterCount);
                        for (int i = 0; i < scope075.CharacterCount; i++)
                        {
                            var c = scope075[i];
                            _clientState.AddOrUpdatePlayerInScope(c.Id, c.CurrentPositionX, c.CurrentPositionY, c.Name);
                            _logger.LogDebug("  -> Character in scope (0.75): ID={Id:X4}, Name='{Name}'", c.Id, c.Name);
                            if (c.Name == characterNameToFind) foundCharacterId = c.Id;
                        }
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for AddCharacterToScope.", TargetVersion);
                        break;
                }

                if (foundCharacterId != 0xFFFF && _clientState.GetCharacterId() == 0xFFFF)
                {
                    _clientState.SetCharacterId(foundCharacterId);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing AddCharactersToScope (12). Packet: {PacketData}", Convert.ToHexString(packet.Span)); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x11, NoSubCode)]
        private Task HandleObjectHitAsync(Memory<byte> packet)
        {
            try
            {
                if (TargetVersion >= TargetProtocolVersion.Season6)
                {
                    var hit = new ObjectHitExtended(packet);

                    if (hit.ObjectId == _clientState.GetCharacterId())
                    {
                        var hpPercent = hit.HealthStatus * 100 / 250;
                        var sdPercent = hit.ShieldStatus * 100 / 250;

                        _logger.LogWarning("💔 You received {DmgHp} HP dmg, {DmgSd} SD dmg. HP={Hp}%, SD={Sd}%",
                            hit.HealthDamage, hit.ShieldDamage, hpPercent, sdPercent);
                    }
                    else
                    {
                        _logger.LogInformation("🎯 Target {Id:X4} received {DmgHp} HP dmg, {DmgSd} SD dmg.",
                            hit.ObjectId, hit.HealthDamage, hit.ShieldDamage);
                    }
                }
                else
                {
                    var hit = new ObjectHit(packet);

                    if (hit.ObjectId == _clientState.GetCharacterId())
                    {
                        _logger.LogWarning("💔 You received {DmgHp} HP dmg, {DmgSd} SD dmg.",
                            hit.HealthDamage, hit.ShieldDamage);

                        _clientState.UpdateCurrentHealthShield(hit.HealthDamage, hit.ShieldDamage);
                    }
                    else
                    {
                        _logger.LogInformation("🎯 Target {Id:X4} received {DmgHp} HP dmg, {DmgSd} SD dmg.",
                            hit.ObjectId, hit.HealthDamage, hit.ShieldDamage);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectHit (0x11)");
            }

            return Task.CompletedTask;
        }

        [PacketHandler(0x13, NoSubCode)]
        private Task HandleAddNpcToScopeAsync(Memory<byte> packet)
        {
            try
            {
                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        var scopeS6 = new AddNpcsToScope(packet);
                        _logger.LogInformation("🤖 Received AddNpcToScope (S6): {Count} NPC.", scopeS6.NpcCount);
                        for (int i = 0; i < scopeS6.NpcCount; i++)
                        {
                            var npc = scopeS6[i];
                            _clientState.AddOrUpdateNpcInScope(npc.Id, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber); // <--- Added call
                            _logger.LogDebug("  -> NPC in scope (S6): ID={Id:X4}, Type={Type}, Pos=({X},{Y})", npc.Id, npc.TypeNumber, npc.CurrentPositionX, npc.CurrentPositionY);
                        }
                        break;
                    case TargetProtocolVersion.Version097:
                        var scope097 = new AddNpcsToScope095(packet);
                        _logger.LogInformation("🤖 Received AddNpcToScope (0.97): {Count} NPC.", scope097.NpcCount);
                        for (int i = 0; i < scope097.NpcCount; i++)
                        {
                            var npc = scope097[i];
                            _clientState.AddOrUpdateNpcInScope(npc.Id, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber); // <--- Added call
                            _logger.LogDebug("  -> NPC in scope (0.97): ID={Id:X4}, Type={Type}, Pos=({X},{Y})", npc.Id, npc.TypeNumber, npc.CurrentPositionX, npc.CurrentPositionY);
                        }
                        break;
                    case TargetProtocolVersion.Version075:
                        var scope075 = new AddNpcsToScope075(packet);
                        _logger.LogInformation("🤖 Received AddNpcToScope (0.75): {Count} NPC.", scope075.NpcCount);
                        for (int i = 0; i < scope075.NpcCount; i++)
                        {
                            var npc = scope075[i];
                            _clientState.AddOrUpdateNpcInScope(npc.Id, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber); // <--- Added call
                            _logger.LogDebug("  -> NPC in scope (0.75): ID={Id:X4}, Type={Type}, Pos=({X},{Y})", npc.Id, npc.TypeNumber, npc.CurrentPositionX, npc.CurrentPositionY);
                        }
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for AddNpcToScope.", TargetVersion);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing AddNpcToScope (13)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x20, NoSubCode)]
        private Task HandleItemsDroppedAsync(Memory<byte> packet)
        {
            try
            {
                // Define the size of the fixed part BEFORE the item list starts
                const int ItemsDroppedFixedHeaderSize = 4; // C2 Header size
                const int ItemsDroppedFixedPrefixSize = ItemsDroppedFixedHeaderSize + 1; // Header + ItemCount byte

                if (TargetVersion >= TargetProtocolVersion.Season6) // Assuming 0.97+ uses similar structure to S6 for 0x20
                {
                    if (packet.Length < ItemsDroppedFixedPrefixSize)
                    {
                        _logger.LogWarning("⚠️ ItemsDropped packet (0x20) too short. Length: {Length}", packet.Length);
                        return Task.CompletedTask;
                    }

                    var droppedItems = new ItemsDropped(packet); // Use the S6 struct
                    _logger.LogInformation("💰 Received ItemsDropped (S6/0.97): {Count} item(s).", droppedItems.ItemCount);

                    int currentOffset = ItemsDroppedFixedPrefixSize; // Start reading items after Header + ItemCount
                    for (int i = 0; i < droppedItems.ItemCount; i++)
                    {
                        // --- Calculate structLength dynamically ---
                        // We need to know the item data length for THIS item.
                        // This is complex without parsing the item data itself to know its real size.
                        // For robust parsing, one would typically read the base DroppedItem,
                        // then parse the ItemData within it to determine its actual size,
                        // then advance the offset.

                        // --- Simplified Approach (Less Robust) ---
                        // Attempt to guess length IF only one item exists in the packet.
                        // This WILL FAIL if multiple items with different data lengths are sent.
                        int itemDataLenGuess = -1;
                        int currentStructSize;
                        if (droppedItems.ItemCount == 1)
                        {
                            itemDataLenGuess = packet.Length - currentOffset - MUnique.OpenMU.Network.Packets.ServerToClient.ItemsDropped.DroppedItem.GetRequiredSize(0);
                            if (itemDataLenGuess < 0)
                            {
                                _logger.LogWarning("  -> Invalid calculated item data length ({Length}) for single item. Skipping.", itemDataLenGuess);
                                break;
                            }
                            currentStructSize = MUnique.OpenMU.Network.Packets.ServerToClient.ItemsDropped.DroppedItem.GetRequiredSize(itemDataLenGuess);
                        }
                        else
                        {
                            // We cannot reliably determine the size for multiple items without full parsing.
                            // We have to make an assumption or skip. Let's assume a default small size or log and skip.
                            // TODO: Implement proper item size detection based on item type/data.
                            _logger.LogWarning("  -> Cannot reliably parse multiple items of potentially variable size in ItemsDropped (0x20). Skipping details for item {Index}.", i);
                            // Or, if you know the MINIMUM item data size (e.g., 7 bytes for older versions?), use that:
                            // itemDataLenGuess = 7; // Example assumption
                            // currentStructSize = ServerToClient.ItemsDropped.DroppedItem.GetRequiredSize(itemDataLenGuess);
                            break; // Exit loop for now if multiple items and unknown size
                        }

                        if (currentOffset + currentStructSize > packet.Length)
                        {
                            _logger.LogWarning("  -> Packet too short for DroppedItem {Index}. Offset: {Offset}, Required: {Required}, TotalLength: {Total}", i, currentOffset, currentStructSize, packet.Length);
                            break;
                        }

                        // Slice the memory for the current item struct
                        var itemMemory = packet.Slice(currentOffset, currentStructSize);
                        var item = new MUnique.OpenMU.Network.Packets.ServerToClient.ItemsDropped.DroppedItem(itemMemory); // Use the sub-struct

                        // Process the item
                        _clientState.AddOrUpdateItemInScope(item.Id, item.PositionX, item.PositionY, item.ItemData);
                        _logger.LogDebug("  -> Dropped Item (S6/0.97): ID={Id:X4}, Pos=({X},{Y}), Fresh={Fresh}, DataLen={DataLen}", item.Id, item.PositionX, item.PositionY, item.IsFreshDrop, item.ItemData.Length);

                        // Advance the offset
                        currentOffset += currentStructSize;
                    }
                }
                else if (TargetVersion == TargetProtocolVersion.Version075)
                {
                    // Use MoneyDropped075 struct for items too in 0.75 for packet 0x20
                    if (packet.Length < MoneyDropped075.Length) // Check against the minimum size of THIS struct
                    {
                        _logger.LogWarning("⚠️ Dropped Object packet (0.75, 0x20) too short. Length: {Length}", packet.Length);
                        return Task.CompletedTask;
                    }

                    var droppedObjectLegacy = new MoneyDropped075(packet);
                    _logger.LogInformation("💰 Received Dropped Object (0.75): Count={Count}.", droppedObjectLegacy.ItemCount);

                    // 0.75 usually sends one item/money drop per packet for 0x20
                    if (droppedObjectLegacy.ItemCount == 1)
                    {
                        ushort id = droppedObjectLegacy.Id;
                        byte x = droppedObjectLegacy.PositionX;
                        byte y = droppedObjectLegacy.PositionY;

                        // Check if it's money based on known item group/index for money
                        // The MoneyDropped075 structure itself has MoneyGroup/MoneyNumber properties
                        if (droppedObjectLegacy.MoneyGroup == 14 && droppedObjectLegacy.MoneyNumber == 15)
                        {
                            uint amount = droppedObjectLegacy.Amount; // Use the extension method from MoneyDropped.txt
                            _clientState.AddOrUpdateMoneyInScope(id, x, y, amount);
                            _logger.LogDebug("  -> Dropped Money (0.75): ID={Id:X4}, Pos=({X},{Y}), Amount={Amount}", id, x, y, amount);
                        }
                        else
                        {
                            // It's an item. The item data is embedded within the MoneyDropped075 structure's layout.
                            // We need to extract the relevant part. The MoneyDropped075 structure has specific offsets.
                            // The item data for 0.75 item structure (usually 7 bytes) starts conceptually after the coordinates (index 8).
                            // Let's assume the item data occupies bytes 9 through 15 (total 7 bytes) within the MoneyDropped075 structure.
                            const int itemDataOffset = 9; // Offset within the MoneyDropped075 structure
                            const int itemDataLength075 = 7; // Standard 0.75 item data length
                            if (MoneyDropped075.Length >= itemDataOffset + itemDataLength075)
                            {
                                ReadOnlySpan<byte> itemData = packet.Span.Slice(itemDataOffset, itemDataLength075);
                                _clientState.AddOrUpdateItemInScope(id, x, y, itemData);
                                _logger.LogDebug("  -> Dropped Item (0.75): ID={Id:X4}, Pos=({X},{Y}), Data={Data}", id, x, y, Convert.ToHexString(itemData));
                            }
                            else
                            {
                                _logger.LogWarning("  -> Could not extract expected item data from Dropped Object packet (0.75).");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("  -> Dropped Object (0.75): Multiple objects in one packet not handled.");
                    }
                }
                else
                {
                    _logger.LogWarning("❓ Unsupported protocol version ({Version}) for ItemsDropped (20).", TargetVersion);
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // Catch index errors which are common with incorrect length calculations
                _logger.LogError(ex, "💥 Index/Range error parsing ItemsDropped (20). Packet Length: {Length}. Packet: {PacketData}", packet.Length, Convert.ToHexString(packet.Span));
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 General error parsing ItemsDropped (20). Packet: {PacketData}", Convert.ToHexString(packet.Span)); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x21, NoSubCode)] // C2 21 00 - ItemDropRemoved
        private Task HandleItemDropRemovedAsync(Memory<byte> packet)
        {
            try
            {
                if (TargetVersion < TargetProtocolVersion.Version097) // Assume 0.97+ uses S6 structure
                {
                    _logger.LogWarning("⚠️ ItemDropRemoved (0x21) handling may differ for version {Version}. Assuming S6+ structure.", TargetVersion);
                    // Potentially add specific handling for older versions if their 0x21 structure is different
                }

                const int ItemDropRemovedFixedHeaderSize = 4; // C2 Header size
                const int ItemDropRemovedFixedPrefixSize = ItemDropRemovedFixedHeaderSize + 1; // Header + ItemCount byte
                const int ItemIdSize = 2; // Size of each DroppedItemId struct

                if (packet.Length < ItemDropRemovedFixedPrefixSize)
                {
                    _logger.LogWarning("⚠️ ItemDropRemoved packet (0x21) too short. Length: {Length}", packet.Length);
                    return Task.CompletedTask;
                }

                var itemDropRemoved = new ItemDropRemoved(packet); // Use the S6 struct
                byte count = itemDropRemoved.ItemCount;
                _logger.LogInformation("🗑️ Received ItemDropRemoved: {Count} item(s).", count);

                if (packet.Length < ItemDropRemovedFixedPrefixSize + count * ItemIdSize)
                {
                    _logger.LogWarning("⚠️ ItemDropRemoved packet (0x21) seems too short for {Count} items. Length: {Length}", count, packet.Length);
                    count = (byte)((packet.Length - ItemDropRemovedFixedPrefixSize) / ItemIdSize);
                    _logger.LogWarning("   -> Adjusting count to {AdjustedCount} based on length.", count);
                }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var droppedItemIdStruct = itemDropRemoved[i]; // Access struct within the packet
                        ushort idToRemoveFromServer = droppedItemIdStruct.Id; // Get the ID sent by the server

                        // --- SERVER BUG WORKAROUND START ---
                        // Some servers incorrectly send a generic ID (like 1 or 2) in the 0x21 packet
                        // instead of the actual item ID (like 8001, 8002).
                        // This workaround attempts to guess the correct item ID *only* if:
                        // 1. The server sent a suspicious low ID (e.g., <= 10).
                        // 2. This packet contains only one removal instruction (count == 1).
                        // 3. There is exactly one Item or Money object currently in the client's scope.
                        // THIS IS UNRELIABLE and should be fixed on the server!
                        ushort actualIdToRemove = idToRemoveFromServer; // Start with the ID from the server
                        bool usedWorkaround = false;

                        if (idToRemoveFromServer <= 10 && count == 1) // Condition 1 & 2
                        {
                            var itemsInScope = _clientState.GetScopeItems(ScopeObjectType.Item).ToList();
                            var moneyInScope = _clientState.GetScopeItems(ScopeObjectType.Money).ToList();

                            if (itemsInScope.Count + moneyInScope.Count == 1) // Condition 3
                            {
                                ScopeObject? singleObject = itemsInScope.FirstOrDefault() ?? moneyInScope.FirstOrDefault();
                                if (singleObject != null)
                                {
                                    actualIdToRemove = singleObject.Id; // Assume this single object is the one to remove
                                    usedWorkaround = true;
                                    _logger.LogWarning("🔧 Applying workaround for ItemDropRemoved: Server sent ID {ServerId:X4}, but only one item/money ({ActualId:X4}) is in scope. Attempting removal of {ActualId:X4}.",
                                        idToRemoveFromServer, actualIdToRemove, actualIdToRemove);
                                }
                            }
                            else if (itemsInScope.Count + moneyInScope.Count > 1)
                            {
                                _logger.LogWarning("🔧 Workaround for ItemDropRemoved skipped: Server sent suspicious ID {ServerId:X4}, but multiple ({Count}) items/money exist in scope. Cannot determine correct target.",
                                     idToRemoveFromServer, itemsInScope.Count + moneyInScope.Count);
                            }
                        }
                        // --- SERVER BUG WORKAROUND END ---


                        // Attempt removal using the potentially corrected ID
                        _clientState.RemoveObjectFromScope(actualIdToRemove); // Use actualIdToRemove

                        if (usedWorkaround)
                        {
                            _logger.LogDebug("  -> Removal attempted using WORKAROUND ID {Id:X4}.", actualIdToRemove);
                        }
                        else
                        {
                            _logger.LogDebug("  -> Removing Item ID {Id:X4} (received from server).", actualIdToRemove);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "💥 Error processing item removal at index {Index} in ItemDropRemoved (21).", i);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ItemDropRemoved (21). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x2F, NoSubCode)] // MoneyDroppedExtended in ServerToClientPackets.txt
        private Task HandleMoneyDroppedExtendedAsync(Memory<byte> packet)
        {
            try
            {
                var moneyDrop = new MoneyDroppedExtended(packet);
                _clientState.AddOrUpdateMoneyInScope(moneyDrop.Id, moneyDrop.PositionX, moneyDrop.PositionY, moneyDrop.Amount);
                _logger.LogInformation("💰 Received MoneyDroppedExtended (2F): ID={Id:X4}, Amount={Amount}, Pos=({X},{Y}), Fresh={Fresh}",
                    moneyDrop.Id, moneyDrop.Amount, moneyDrop.PositionX, moneyDrop.PositionY, moneyDrop.IsFreshDrop);
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MoneyDroppedExtended (2F)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x14, NoSubCode)]
        private Task HandleMapObjectOutOfScopeAsync(Memory<byte> packet)
        {
            try
            {
                var outOfScopePacket = new MapObjectOutOfScope(packet);
                int count = outOfScopePacket.ObjectCount;
                // _logger.LogInformation("🔭 Objects out of scope ({Count}):", count); // Moved log inside loop
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort objectId = outOfScopePacket[i].Id;
                        _clientState.RemoveObjectFromScope(objectId); // Call remove method
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MapObjectOutOfScope (14). Packet: {PacketData}", Convert.ToHexString(packet.Span)); }
            return Task.CompletedTask;
        }


        [PacketHandler(0x15, NoSubCode)] // ObjectMoved
        private Task HandleObjectMovedAsync(Memory<byte> packet)
        {
            try
            {
                var move = new ObjectMoved(packet);
                ushort objectId = move.ObjectId;
                byte x = move.PositionX;
                byte y = move.PositionY;

                _logger.LogDebug("   -> Received ObjectMoved (0x15): ID={Id:X4} -> ({X}, {Y})", objectId, x, y);

                // Update position in scope if the object exists
                if (_clientState.TryUpdateScopeObjectPosition(objectId, x, y))
                {
                    _logger.LogTrace("   -> Updated position for {Id:X4} in scope.", objectId);
                }
                else
                {
                    // It's possible to receive a move for an object not yet in scope (race condition)
                    // Or for an object we don't track (like maybe projectiles?)
                    _logger.LogTrace("   -> Object {Id:X4} not found in scope for position update (or not tracked).", objectId);
                }

                // Player specific logic
                if (objectId == _clientState.GetCharacterId())
                {
                    _logger.LogInformation("🏃‍♂️ Character teleported/moved to ({X}, {Y}) via 0x15", x, y);
                    _clientState.SetPosition(x, y); // Update client's main position
                    _clientState.SignalMovementHandled(); // Ensure walk lock is released if this was the confirmation
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ObjectMoved (15)."); }
            finally { _clientState.SignalMovementHandled(); } // Ensure walk lock is always released after a move confirmation/attempt
            return Task.CompletedTask;
        }

        [PacketHandler(0xD4, NoSubCode)] // ObjectWalked
        private Task HandleObjectWalkedAsync(Memory<byte> packet)
        {
            // Keep existing parsing logic from previous step...
            _logger.LogDebug("🚶 Handling ObjectWalked (D4). Raw Packet Data: {PacketData}", Convert.ToHexString(packet.Span));
            const string forcedFormat = "Standard (Forced for D4)";

            try
            {
                const int minStandardLength = 8; // C1 Header (3) + SourceX/Y (2) + Step/Rot (1) + First Step Byte (1) if StepCount >= 1
                if (packet.Length < 6) // Absolute minimum C1+SrcX/Y+Step/Rot
                {
                    _logger.LogWarning("Received walk packet (D4) too short for base info. Length: {Length}. Packet: {PacketData}", packet.Length, Convert.ToHexString(packet.Span));
                    return Task.CompletedTask;
                }

                var walkStandard = new ObjectWalked(packet); // Assuming S6 structure works for parsing header info for older versions too
                ushort objectId = walkStandard.ObjectId;
                byte targetX = walkStandard.TargetX;
                byte targetY = walkStandard.TargetY;
                byte stepCount = walkStandard.StepCount;


                // --- Log direction ---
                ReadOnlySpan<byte> stepsDataSpan = walkStandard.StepData;
                byte firstStepDirection = 0xFF;
                if (stepCount > 0 && stepsDataSpan.Length > 0)
                {
                    byte firstStepByte = stepsDataSpan[0];
                    firstStepDirection = (byte)((firstStepByte >> 4) & 0x0F);
                }
                string firstStepDirStr = firstStepDirection <= 7 ? firstStepDirection.ToString() : "None";
                // --- End Log direction ---

                // Update position in scope if the object exists
                if (_clientState.TryUpdateScopeObjectPosition(objectId, targetX, targetY))
                {
                    _logger.LogTrace("   -> Updated position for {Id:X4} in scope via walk packet.", objectId);
                }
                else
                {
                    _logger.LogTrace("   -> Object {Id:X4} not found in scope for walk update (or not tracked).", objectId);
                }


                if (objectId == _clientState.GetCharacterId())
                {
                    if (stepCount > 0)
                    {
                        _logger.LogInformation("🚶‍➡️ Character walking -> [Server Target:({TargetX},{TargetY})] Steps:{Steps} ({Version}) 1stStep:{Dir}", targetX, targetY, stepCount, forcedFormat, firstStepDirStr);
                        _clientState.SetPosition(targetX, targetY); // Update client's main position
                                                                    // Note: SignalMovementHandled should be called ONLY when the walk sequence is *confirmed* finished
                                                                    // by the server, often via a final 0x15 packet or potentially a 0xD4 with StepCount=0.
                                                                    // We call it below in finally, but ideally, it should wait for the *correct* confirmation.
                    }
                    else
                    {
                        // A D4 with StepCount=0 might signify the end of a walk OR just a rotation update.
                        // If it confirms the end position, update and signal.
                        _logger.LogInformation("🚶‍➡️ Character walk ended/rotated at ({TargetX},{TargetY}) via 0xD4 (Steps=0)", targetX, targetY);
                        _clientState.SetPosition(targetX, targetY);
                        _clientState.SignalMovementHandled(); // Treat D4 Step=0 as end confirmation for now
                    }
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) walking -> [Server Target:({TargetX},{TargetY})] Steps:{Steps} ({Version}) 1stStep:{Dir}", objectId, targetX, targetY, stepCount, forcedFormat, firstStepDirStr);
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.LogError(ex, "💥 Range error while parsing ObjectWalked (D4) as {Format}. Likely unexpected packet length ({Length}). Packet: {PacketData}", forcedFormat, packet.Length, Convert.ToHexString(packet.Span));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectWalked (D4) as {Format}. Packet: {PacketData}", forcedFormat, Convert.ToHexString(packet.Span));
            }
            finally
            {
                // Tentative: Release walk lock after processing ANY D4 for our character.
                // This might be too early if the server sends multiple D4s for one walk request.
                // A better approach involves tracking expected steps or waiting for 0x15.
                if (packet.Length > 4) // Basic check
                {
                    ushort objectId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Span.Slice(3, 2));
                    if (objectId == _clientState.GetCharacterId())
                    {
                        // Still handle signalling inside the try block based on StepCount
                        // _clientState.SignalMovementHandled(); // Moved signaling logic inside try block
                    }
                }
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x18, NoSubCode)]
        private Task HandleObjectAnimationAsync(Memory<byte> packet)
        {
            try
            {
                var animation = new ObjectAnimation(packet);
                string animDesc = animation.Animation == 0 ? "Stop" : $"Anim={animation.Animation}";
                if (animation.ObjectId == _clientState.GetCharacterId())
                {
                    _logger.LogInformation("🤺 Our character -> {AnimDesc}, Direction={Dir}, TargetID={Target:X4}", animDesc, animation.Direction, animation.TargetId);
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) -> {AnimDesc}, Direction={Dir}, TargetID={Target:X4}", animation.ObjectId, animDesc, animation.Direction, animation.TargetId);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ObjectAnimation (18)."); }
            return Task.CompletedTask;
        }

#pragma warning restore IDE0051 // Restore warning checks for unused private members

    }
}