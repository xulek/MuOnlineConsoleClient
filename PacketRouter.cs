using System.Buffers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ServerToClient;

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

        public PacketRouter(ILogger<PacketRouter> logger, CharacterService characterService, LoginService loginService, TargetProtocolVersion targetVersion, SimpleLoginClient clientState)
        {
            _logger = logger;
            _characterService = characterService;
            TargetVersion = targetVersion;
            _clientState = clientState;

            RegisterAttributeBasedHandlers();
        }

        public Task RoutePacketAsync(ReadOnlySequence<byte> sequence)
        {
            var packet = sequence.ToArray();
            _logger.LogDebug("📬 Received packet ({Length} bytes): {Data}", packet.Length, Convert.ToHexString(packet));

            if (!TryParsePacketHeader(packet, out byte headerType, out byte code, out byte? subCode, out Memory<byte> packetMemory))
            {
                _logger.LogWarning("❓ Failed to parse packet header: {Data}", Convert.ToHexString(packet));
                return Task.CompletedTask;
            }

            _logger.LogDebug("🔎 Parsing: Header={HeaderType:X2}, Code={Code:X2}, SubCode={SubCode}",
                headerType, code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");

            return DispatchPacketInternalAsync(packetMemory, code, subCode, headerType);
        }

        private Task DispatchPacketInternalAsync(Memory<byte> packet, byte code, byte? subCode, byte headerType)
        {
            byte lookupSubCode = subCode ?? NoSubCode;
            var handlerKey = (code, lookupSubCode);

            if (_packetHandlers.TryGetValue(handlerKey, out var handler))
            {
                return ExecuteHandler(handler, packet, code, lookupSubCode);
            }

            if (lookupSubCode != NoSubCode && _packetHandlers.TryGetValue((code, NoSubCode), out var mainCodeHandler))
            {
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
                if (code == 0xD4 || code == 0x15)
                {
                    _clientState.SignalMovementHandled();
                }
            }
        }

        public Task OnDisconnected()
        {
            _logger.LogWarning("🔌 Disconnected from server.");
            _clientState.SetInGameStatus(false);
            return Task.CompletedTask;
        }

        private void RegisterAttributeBasedHandlers()
        {
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
            _logger.LogInformation("✅ Packet handler registration complete. {Count} handlers registered.", count);
        }

        private bool TryParsePacketHeader(byte[] packet, out byte headerType, out byte code, out byte? subCode, out Memory<byte> packetMemory)
        {
            headerType = 0; code = 0; subCode = null; packetMemory = packet;
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
                        code = packet[3];
                        subCode = packet.Length >= 5 && SubCodeHolder.ContainsSubCode(code) ? packet[4] : (byte?)null;
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
            _logger.LogWarning("⚠️ Unhandled packet: Code={Code:X2} SubCode={SubCode}", code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");
        }

        private void LogUnexpectedHeader(byte headerType, byte code, byte? subCode)
        {
            _logger.LogWarning("❓ Unexpected Header {Header:X2} for packet Code={Code:X2} SubCode={SubCode}", headerType, code, subCode.HasValue ? subCode.Value.ToString("X2") : "N/A");
        }

        // ==================================================
        //  Packet Handlers (Attribute Marked)
        // ==================================================

#pragma warning disable IDE0051 // Disable warning for unused private members, as they are used by reflection via PacketHandlerAttribute

        [PacketHandler(0xF1, 0x00)]
        private Task HandleGameServerEnteredAsync(Memory<byte> packet)
        {
            _logger.LogInformation("➡️🚪 Received GameServerEntered (F1, 00).");
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
                switch (TargetVersion)
                {
                    case TargetProtocolVersion.Season6:
                        var mapChangeS6 = new MapChanged(packet);
                        posX = mapChangeS6.PositionX;
                        posY = mapChangeS6.PositionY;
                        _logger.LogInformation("🗺️ Received MapChanged (S6): MapNumber={MapNumber}, Pos=({X},{Y}), IsMapChange={IsChange}", mapChangeS6.MapNumber, mapChangeS6.PositionX, mapChangeS6.PositionY, mapChangeS6.IsMapChange);
                        break;
                    case TargetProtocolVersion.Version097:
                    case TargetProtocolVersion.Version075:
                        var mapChangeLegacy = new MapChanged075(packet);
                        posX = mapChangeLegacy.PositionX;
                        posY = mapChangeLegacy.PositionY;
                        _logger.LogInformation("🗺️ Received MapChanged ({Version}): MapNumber={MapNumber}, Pos=({X},{Y}), IsMapChange={IsChange}", TargetVersion, mapChangeLegacy.MapNumber, mapChangeLegacy.PositionX, mapChangeLegacy.PositionY, mapChangeLegacy.IsMapChange);
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for MapChanged.", TargetVersion);
                        break;
                }
                if (posX != 0 || posY != 0) { _clientState.SetPosition(posX, posY); }
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
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing WeatherStatusUpdate (0F)."); }
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
                            string name = packet.Span.Slice(currentOffset + 22, 10).ExtractString(0, 10, Encoding.UTF8);
                            byte effectCount = baseCharReadOnlySpan[baseCharacterDataSizeS6 - 1];
                            int fullCharacterSize = baseCharacterDataSizeS6 + effectCount;

                            if (currentOffset + fullCharacterSize > packetSpanS6.Length)
                            {
                                _logger.LogWarning("Insufficient data for full character {Index} (with {EffectCount} effects) in AddCharactersToScope (S6). Offset: {Offset}, Required: {Required}, Length: {Length}", i, effectCount, currentOffset, fullCharacterSize, packetSpanS6.Length);
                                break;
                            }

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
                        break;
                    case TargetProtocolVersion.Version097:
                        var scope097 = new AddNpcsToScope095(packet);
                        _logger.LogInformation("🤖 Received AddNpcToScope (0.97): {Count} NPC.", scope097.NpcCount);
                        break;
                    case TargetProtocolVersion.Version075:
                        var scope075 = new AddNpcsToScope075(packet);
                        _logger.LogInformation("🤖 Received AddNpcToScope (0.75): {Count} NPC.", scope075.NpcCount);
                        break;
                    default:
                        _logger.LogWarning("❓ Unsupported protocol version ({Version}) for AddNpcToScope.", TargetVersion);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing AddNpcToScope (13)."); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x15, NoSubCode)]
        private Task HandleObjectMovedAsync(Memory<byte> packet)
        {
            try
            {
                var move = new ObjectMoved(packet);
                ushort objectId = move.ObjectId;
                byte x = move.PositionX;
                byte y = move.PositionY;

                _logger.LogDebug("   -> Server reported position in 0x15: ({X}, {Y}) for ObjectId {Id:X4}", x, y, objectId);

                if (objectId == _clientState.GetCharacterId())
                {
                    _logger.LogInformation("🏃‍♂️ Character teleported/moved to ({X}, {Y})", x, y);
                    _clientState.SetPosition(x, y);
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) moved to ({X}, {Y})", objectId, x, y);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing ObjectMoved (15)."); _clientState.SignalMovementHandled(); }
            return Task.CompletedTask;
        }

        [PacketHandler(0xD4, NoSubCode)]
        private Task HandleObjectWalkedAsync(Memory<byte> packet)
        {
            _logger.LogDebug("🚶 Handling ObjectWalked (D4). Raw Packet Data: {PacketData}", Convert.ToHexString(packet.Span));
            const string forcedFormat = "Standard (Forced for D4)";

            try
            {
                const int minStandardLength = 8;
                if (packet.Length < minStandardLength)
                {
                    _logger.LogWarning("Received walk packet (D4) too short for Standard format. Length: {Length}. Packet: {PacketData}", packet.Length, Convert.ToHexString(packet.Span));
                    return Task.CompletedTask;
                }

                var walkStandard = new ObjectWalked(packet);
                ushort objectId = walkStandard.ObjectId;
                byte targetX = walkStandard.TargetX;
                byte targetY = walkStandard.TargetY;
                byte stepCount = walkStandard.StepCount;
                ReadOnlySpan<byte> stepsDataSpan = walkStandard.StepData;

                byte firstStepDirection = 0xFF;
                if (stepCount > 0 && stepsDataSpan.Length > 0)
                {
                    byte firstStepByte = stepsDataSpan[0];
                    firstStepDirection = (byte)((firstStepByte >> 4) & 0x0F);
                }

                string firstStepDirStr = firstStepDirection <= 7 ? firstStepDirection.ToString() : "None";

                if (objectId == _clientState.GetCharacterId())
                {
                    if (stepCount > 0)
                    {
                        _logger.LogInformation("🚶‍➡️ Character walking (Steps > 0) -> [Target Route according to server:({TargetX},{TargetY})] Steps:{Steps} ({Version}) First Step:{Dir}", targetX, targetY, stepCount, forcedFormat, firstStepDirStr);
                        _clientState.SetPosition(targetX, targetY);
                    }
                    else
                    {
                        _logger.LogInformation("🚶‍➡️ Received D4 packet for character, but Steps=0. Position NOT updated. Target according to server: ({TargetX},{TargetY}). Waiting for possible 0x15.", targetX, targetY);
                        _clientState.SignalMovementHandled();
                    }
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) walking -> [Target Route according to server:({TargetX},{TargetY})] Steps:{Steps} ({Version}) First Step:{Dir}", objectId, targetX, targetY, stepCount, forcedFormat, firstStepDirStr);
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.LogError(ex, "💥 Range error while parsing ObjectWalked (D4) as {Format}. Likely unexpected packet length ({Length}). Packet: {PacketData}", forcedFormat, packet.Length, Convert.ToHexString(packet.Span));
                _clientState.SignalMovementHandled();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectWalked (D4) as {Format}. Packet: {PacketData}", forcedFormat, Convert.ToHexString(packet.Span));
                _clientState.SignalMovementHandled();
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

        [PacketHandler(0x14, NoSubCode)]
        private Task HandleMapObjectOutOfScopeAsync(Memory<byte> packet)
        {
            try
            {
                var outOfScopePacket = new MapObjectOutOfScope(packet);
                int count = outOfScopePacket.ObjectCount;
                _logger.LogInformation("🔭 Objects out of scope ({Count}):", count);
                if (count > 0)
                {
                    var ids = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        ushort objectId = outOfScopePacket[i].Id;
                        ids.Add($"{objectId:X4}");
                    }
                    _logger.LogInformation("   -> ID: {ObjectIds}", string.Join(", ", ids));
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MapObjectOutOfScope (14). Packet: {PacketData}", Convert.ToHexString(packet.Span)); }
            return Task.CompletedTask;
        }

#pragma warning restore IDE0051 // Restore warning checks for unused private members

    }
}