using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MuOnlineConsole.Client; // For CharacterState, SimpleLoginClient, TargetProtocolVersion
using MuOnlineConsole.Core.Utilities; // For PacketHandlerAttribute, ItemDatabase
using MuOnlineConsole.Core.Models; // For ScopeObjectType

namespace MuOnlineConsole.Networking.PacketHandling.Handlers
{
    /// <summary>
    /// Handles packets related to objects entering/leaving scope, moving, and dying.
    /// </summary>
    public class ScopeHandler : IGamePacketHandler
    {
        private readonly ILogger<ScopeHandler> _logger;
        private readonly ScopeManager _scopeManager;
        private readonly CharacterState _characterState;
        private readonly SimpleLoginClient _client; // Needed for movement handling and console title
        private readonly TargetProtocolVersion _targetVersion;

        public ScopeHandler(ILoggerFactory loggerFactory, ScopeManager scopeManager, CharacterState characterState, SimpleLoginClient client, TargetProtocolVersion targetVersion)
        {
            _logger = loggerFactory.CreateLogger<ScopeHandler>();
            _scopeManager = scopeManager;
            _characterState = characterState;
            _client = client;
            _targetVersion = targetVersion;
        }

        [PacketHandler(0x12, PacketRouter.NoSubCode)] // AddCharacterToScope
        public Task HandleAddCharacterToScopeAsync(Memory<byte> packet)
        {
            try
            {
                ParseAndAddCharactersToScope(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error processing AddCharactersToScope (12). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        private void ParseAndAddCharactersToScope(Memory<byte> packet)
        {
            ushort foundCharacterId = 0xFFFF;
            string characterNameToFind = _characterState.Name;
            switch (_targetVersion)
            {
                case TargetProtocolVersion.Season6:
                    var scopeS6 = new AddCharactersToScopeRef(packet.Span);
                    if (packet.Length < scopeS6.FinalSize)
                    {
                        _logger.LogWarning("AddCharactersToScope (S6) packet too short (Length: {Length}, Expected: {Expected})", packet.Length, scopeS6.FinalSize);
                        return;
                    }
                    _logger.LogInformation("👀 Received AddCharactersToScope (S6): {Count} characters.", scopeS6.CharacterCount);
                    for (int i = 0; i < scopeS6.CharacterCount; i++)
                    {
                        var c = scopeS6[i];
                        ushort rawId = c.Id;
                        ushort maskedId = (ushort)(rawId & 0x7FFF);
                        _scopeManager.AddOrUpdatePlayerInScope(maskedId, rawId, c.CurrentPositionX, c.CurrentPositionY, c.Name);
                        if (c.Name == characterNameToFind && _characterState.Id == 0xFFFF)
                        {
                            _logger.LogInformation("👀 Player '{Name}' (ID: {MaskedId:X4}) appeared at ({X},{Y}).", c.Name, maskedId, c.CurrentPositionX, c.CurrentPositionY);
                            foundCharacterId = maskedId;
                        }
                    }
                    break;
                case TargetProtocolVersion.Version097:
                    var scope097 = new AddCharactersToScope095(packet);
                    _logger.LogInformation("👀 Received AddCharactersToScope (0.97): {Count} characters.", scope097.CharacterCount);
                    for (int i = 0; i < scope097.CharacterCount; i++)
                    {
                        var c = scope097[i];
                        ushort rawId097 = c.Id;
                        ushort maskedId097 = (ushort)(rawId097 & 0x7FFF);
                        _scopeManager.AddOrUpdatePlayerInScope(maskedId097, rawId097, c.CurrentPositionX, c.CurrentPositionY, c.Name);
                        if (c.Name == characterNameToFind && _characterState.Id == 0xFFFF)
                        {
                            _logger.LogInformation("👀 Player '{Name}' (ID: {MaskedId:X4}) appeared at ({X},{Y}).", c.Name, maskedId097, c.CurrentPositionX, c.CurrentPositionY);
                            foundCharacterId = maskedId097;
                        }
                    }
                    break;
                case TargetProtocolVersion.Version075:
                    var scope075 = new AddCharactersToScope075(packet);
                    _logger.LogInformation("👀 Received AddCharactersToScope (0.75): {Count} characters.", scope075.CharacterCount);
                    for (int i = 0; i < scope075.CharacterCount; i++)
                    {
                        var c = scope075[i];
                        ushort rawId075 = c.Id;
                        ushort maskedId075 = (ushort)(rawId075 & 0x7FFF);
                        _scopeManager.AddOrUpdatePlayerInScope(maskedId075, rawId075, c.CurrentPositionX, c.CurrentPositionY, c.Name);
                        if (c.Name == characterNameToFind && _characterState.Id == 0xFFFF)
                        {
                            _logger.LogInformation("👀 Player '{Name}' (ID: {MaskedId:X4}) appeared at ({X},{Y}).", c.Name, maskedId075, c.CurrentPositionX, c.CurrentPositionY);
                            foundCharacterId = maskedId075;
                        }
                    }
                    break;
                default:
                    _logger.LogWarning("❓ Unsupported protocol version ({Version}) for AddCharacterToScope.", _targetVersion);
                    break;
            }
            if (foundCharacterId != 0xFFFF && _characterState.Id == 0xFFFF)
            {
                _characterState.Id = foundCharacterId;
                _logger.LogInformation("🆔 Character ID set: {CharacterId:X4}", _characterState.Id);
                _client.UpdateConsoleTitle();
            }
        }

        [PacketHandler(0x13, PacketRouter.NoSubCode)] // AddNpcToScope
        public Task HandleAddNpcToScopeAsync(Memory<byte> packet)
        {
            try
            {
                ParseAndAddNpcsToScope(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error processing AddNpcToScope (13). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        private void ParseAndAddNpcsToScope(Memory<byte> packet)
        {
            switch (_targetVersion)
            {
                case TargetProtocolVersion.Season6:
                    var scopeS6 = new AddNpcsToScope(packet);
                    _logger.LogInformation("🤖 Received AddNpcToScope (S6): {Count} NPC(s).", scopeS6.NpcCount);
                    for (int i = 0; i < scopeS6.NpcCount; i++)
                    {
                        ushort typeNumber;
                        byte currentX, currentY;
                        var npc = scopeS6[i];
                        ushort rawIdS6 = npc.Id;
                        ushort maskedIdS6 = (ushort)(rawIdS6 & 0x7FFF);
                        _scopeManager.AddOrUpdateNpcInScope(maskedIdS6, rawIdS6, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber);
                        typeNumber = npc.TypeNumber;
                        currentX = npc.CurrentPositionX;
                        currentY = npc.CurrentPositionY;
                        string npcName = NpcDatabase.GetNpcName(typeNumber);
                        _logger.LogInformation("👀 {NpcDesignation} (ID: {MaskedId:X4}) appeared at ({X},{Y}).", npcName, maskedIdS6, currentX, currentY);
                    }
                    break;
                case TargetProtocolVersion.Version097:
                    var scope097 = new AddNpcsToScope095(packet);
                    _logger.LogInformation("🤖 Received AddNpcToScope (0.97): {Count} NPC(s).", scope097.NpcCount);
                    for (int i = 0; i < scope097.NpcCount; i++)
                    {
                        ushort typeNumber;
                        byte currentX, currentY;
                        var npc = scope097[i];
                        ushort rawId097 = npc.Id;
                        ushort maskedId097 = (ushort)(rawId097 & 0x7FFF);
                        _scopeManager.AddOrUpdateNpcInScope(maskedId097, rawId097, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber);
                        typeNumber = npc.TypeNumber;
                        currentX = npc.CurrentPositionX;
                        currentY = npc.CurrentPositionY;
                        string npcName = NpcDatabase.GetNpcName(typeNumber);
                        _logger.LogInformation("👀 {NpcDesignation} (ID: {MaskedId:X4}) appeared at ({X},{Y}).", npcName, maskedId097, currentX, currentY);
                    }
                    break;
                case TargetProtocolVersion.Version075:
                    var scope075 = new AddNpcsToScope075(packet);
                    _logger.LogInformation("🤖 Received AddNpcToScope (0.75): {Count} NPC(s).", scope075.NpcCount);
                    for (int i = 0; i < scope075.NpcCount; i++)
                    {
                        ushort typeNumber;
                        byte currentX, currentY;
                        var npc = scope075[i];
                        ushort rawId075 = npc.Id;
                        ushort maskedId075 = (ushort)(rawId075 & 0x7FFF);
                        _scopeManager.AddOrUpdateNpcInScope(maskedId075, rawId075, npc.CurrentPositionX, npc.CurrentPositionY, npc.TypeNumber);
                        // Use NpcDatabase for logging
                        typeNumber = npc.TypeNumber;
                        currentX = npc.CurrentPositionX;
                        currentY = npc.CurrentPositionY;
                        string npcName = NpcDatabase.GetNpcName(typeNumber);
                        _logger.LogInformation("👀 {NpcDesignation} (ID: {MaskedId:X4}) appeared at ({X},{Y}).", npcName, maskedId075, currentX, currentY);
                    }
                    break;
                default:
                    _logger.LogWarning("❓ Unsupported protocol version ({Version}) for AddNpcToScope.", _targetVersion);
                    break;
            }
        }

        [PacketHandler(0x20, PacketRouter.NoSubCode)] // ItemsDropped / MoneyDropped075
        public Task HandleItemsDroppedAsync(Memory<byte> packet)
        {
            try
            {
                ParseAndAddDroppedItemsToScope(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error processing ItemsDropped (20). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        private void ParseAndAddDroppedItemsToScope(Memory<byte> packet)
        {
            const int ItemsDroppedFixedHeaderSize = 4;
            const int ItemsDroppedFixedPrefixSize = ItemsDroppedFixedHeaderSize + 1;
            if (_targetVersion >= TargetProtocolVersion.Season6)
            {
                if (packet.Length < ItemsDroppedFixedPrefixSize)
                {
                    _logger.LogWarning("⚠️ ItemsDropped packet (0x20, S6+) too short for header. Length: {Length}", packet.Length);
                    return;
                }
                var droppedItems = new ItemsDropped(packet);
                _logger.LogInformation("💰 Received ItemsDropped (S6/0.97): {Count} item(s).", droppedItems.ItemCount);
                int currentOffset = ItemsDroppedFixedPrefixSize;
                for (int i = 0; i < droppedItems.ItemCount; i++)
                {
                    const int assumedItemDataLengthS6 = 12;
                    int currentStructSize = ItemsDropped.DroppedItem.GetRequiredSize(assumedItemDataLengthS6);
                    if (currentOffset + currentStructSize > packet.Length)
                    {
                        _logger.LogWarning("  -> Packet too short for assumed DroppedItem {Index} size.", i);
                        break;
                    }
                    var itemMemory = packet.Slice(currentOffset, currentStructSize);
                    var item = new ItemsDropped.DroppedItem(itemMemory);
                    ushort rawId = item.Id;
                    ushort maskedId = (ushort)(rawId & 0x7FFF);
                    ReadOnlySpan<byte> itemData = item.ItemData;
                    bool isMoney = itemData.Length >= 6 && itemData[0] == 15 && (itemData[5] >> 4) == 14;
                    uint moneyAmount = 0;
                    if (isMoney && itemData.Length >= 5)
                    {
                        moneyAmount = itemData[4];
                    }
                    if (isMoney)
                    {
                        _scopeManager.AddOrUpdateMoneyInScope(maskedId, rawId, item.PositionX, item.PositionY, moneyAmount);
                        _logger.LogDebug("  -> Dropped Money (S6/0.97): Amount={Amount}, RawID={RawId:X4}, MaskedID={MaskedId:X4}, Pos=({X},{Y})", moneyAmount, rawId, maskedId, item.PositionX, item.PositionY);
                    }
                    else
                    {
                        _scopeManager.AddOrUpdateItemInScope(maskedId, rawId, item.PositionX, item.PositionY, itemData);
                        _logger.LogDebug("  -> Dropped Item (S6/0.97): RawID={RawId:X4}, MaskedID={MaskedId:X4}, Pos=({X},{Y}), DataLen={DataLen}", rawId, maskedId, item.PositionX, item.PositionY, itemData.Length);
                    }
                    currentOffset += currentStructSize;
                }
            }
            else if (_targetVersion == TargetProtocolVersion.Version075)
            {
                if (packet.Length < MoneyDropped075.Length)
                {
                    _logger.LogWarning("⚠️ Dropped Object packet (0.75, 0x20) too short. Length: {Length}", packet.Length);
                    return;
                }
                var droppedObjectLegacy = new MoneyDropped075(packet);
                _logger.LogInformation("💰 Received Dropped Object (0.75): Count={Count}.", droppedObjectLegacy.ItemCount);
                if (droppedObjectLegacy.ItemCount == 1)
                {
                    ushort rawId = droppedObjectLegacy.Id;
                    ushort maskedId = (ushort)(rawId & 0x7FFF);
                    byte x = droppedObjectLegacy.PositionX;
                    byte y = droppedObjectLegacy.PositionY;
                    if (droppedObjectLegacy.MoneyGroup == 14 && droppedObjectLegacy.MoneyNumber == 15)
                    {
                        uint amount = droppedObjectLegacy.Amount;
                        _scopeManager.AddOrUpdateMoneyInScope(maskedId, rawId, x, y, amount);
                        _logger.LogDebug("  -> Dropped Money (0.75): RawID={RawId:X4}, MaskedID={MaskedId:X4}, Pos=({X},{Y}), Amount={Amount}", rawId, maskedId, x, y, amount);
                    }
                    else
                    {
                        const int itemDataOffset = 9;
                        const int itemDataLength075 = 7;
                        if (packet.Length >= itemDataOffset + itemDataLength075)
                        {
                            ReadOnlySpan<byte> itemData = packet.Span.Slice(itemDataOffset, itemDataLength075);
                            _scopeManager.AddOrUpdateItemInScope(maskedId, rawId, x, y, itemData);
                            _logger.LogDebug("  -> Dropped Item (0.75): RawID={RawId:X4}, MaskedID={MaskedId:X4}, Pos=({X},{Y}), DataLen={DataLen}", rawId, maskedId, x, y, itemData.Length);
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
                _logger.LogWarning("❓ Unsupported protocol version ({Version}) for ItemsDropped (20).", _targetVersion);
            }
        }

        [PacketHandler(0x21, PacketRouter.NoSubCode)] // ItemDropRemoved
        public Task HandleItemDropRemovedAsync(Memory<byte> packet)
        {
            try
            {
                ParseAndRemoveDroppedItemsFromScope(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error processing ItemDropRemoved (21). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        private void ParseAndRemoveDroppedItemsFromScope(Memory<byte> packet)
        {
            const int ItemDropRemovedFixedHeaderSize = 4;
            const int ItemDropRemovedFixedPrefixSize = ItemDropRemovedFixedHeaderSize + 1;
            const int ItemIdSize = 2;
            if (packet.Length < ItemDropRemovedFixedPrefixSize)
            {
                _logger.LogWarning("⚠️ ItemDropRemoved packet (0x21) too short for header. Length: {Length}", packet.Length);
                return;
            }
            var itemDropRemoved = new ItemDropRemoved(packet);
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
                    var droppedItemIdStruct = itemDropRemoved[i];
                    ushort idFromServerRaw = droppedItemIdStruct.Id;
                    ushort idFromServerMasked = (ushort)(idFromServerRaw & 0x7FFF);

                    string itemName = "Item/Zen"; // Default
                    if (_scopeManager.TryGetScopeObjectName(idFromServerRaw, out var name))
                    {
                        itemName = name ?? itemName;
                    }

                    if (!_scopeManager.RemoveObjectFromScope(idFromServerMasked))
                    {
                        _logger.LogWarning("  -> Failed to remove {ItemName}: Server RawID {RawId:X4}, MaskedID {MaskedId:X4}. Item not found in scope.", itemName, idFromServerRaw, idFromServerMasked);
                    }
                    else
                    {
                        // Log successful removal
                        _logger.LogInformation("💨 {ItemName} (ID: {MaskedId:X4}) disappeared from view.", itemName, idFromServerMasked);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "💥 Error processing item removal at index {Index} in ItemDropRemoved (21).", i);
                }
            }
        }

        [PacketHandler(0x2F, PacketRouter.NoSubCode)] // MoneyDroppedExtended
        public Task HandleMoneyDroppedExtendedAsync(Memory<byte> packet)
        {
            try
            {
                if (packet.Length < MoneyDroppedExtended.Length)
                {
                    _logger.LogWarning("⚠️ MoneyDroppedExtended packet (0x2F) too short. Length: {Length}", packet.Length);
                    return Task.CompletedTask;
                }
                var moneyDrop = new MoneyDroppedExtended(packet);
                ushort rawId = moneyDrop.Id;
                ushort maskedId = (ushort)(rawId & 0x7FFF);
                _scopeManager.AddOrUpdateMoneyInScope(maskedId, rawId, moneyDrop.PositionX, moneyDrop.PositionY, moneyDrop.Amount);
                _logger.LogInformation("💰 Received MoneyDroppedExtended (2F): RawID={RawId:X4}, MaskedID={MaskedId:X4}, Amount={Amount}, Pos=({X},{Y})", rawId, maskedId, moneyDrop.Amount, moneyDrop.PositionX, moneyDrop.PositionY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing MoneyDroppedExtended (2F). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x14, PacketRouter.NoSubCode)] // MapObjectOutOfScope
        public Task HandleMapObjectOutOfScopeAsync(Memory<byte> packet)
        {
            try
            {
                var outOfScopePacket = new MapObjectOutOfScope(packet); int count = outOfScopePacket.ObjectCount;
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort objectIdRaw = outOfScopePacket[i].Id;
                        ushort objectIdMasked = (ushort)(objectIdRaw & 0x7FFF);
                        // Try to get a name before removing
                        string objectName = "Object";
                        if (_scopeManager.TryGetScopeObjectName(objectIdRaw, out var name))
                        {
                            objectName = name ?? objectName;
                        }

                        if (_scopeManager.RemoveObjectFromScope(objectIdMasked))
                        {
                            _logger.LogInformation("💨 {ObjectName} (ID: {MaskedId:X4}) went out of scope.", objectName, objectIdMasked);
                        }
                        else
                        {
                            // Optional: Log if removal failed, might indicate state inconsistency
                            _logger.LogDebug("Attempted to remove object {MaskedId:X4} from scope (Out of Scope packet), but it was not found.", objectIdMasked);
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "💥 Error parsing MapObjectOutOfScope (14). Packet: {PacketData}", Convert.ToHexString(packet.Span)); }
            return Task.CompletedTask;
        }

        [PacketHandler(0x15, PacketRouter.NoSubCode)] // ObjectMoved (Instant Move / Teleport)
        public Task HandleObjectMovedAsync(Memory<byte> packet)
        {
            ushort objectIdMasked = 0xFFFF;
            try
            {
                var move = new ObjectMoved(packet);
                ushort objectIdRaw = move.ObjectId;
                objectIdMasked = (ushort)(objectIdRaw & 0x7FFF);
                byte x = move.PositionX;
                byte y = move.PositionY;
                _logger.LogDebug("   -> Received ObjectMoved (0x15): RawID={RawId:X4}, MaskedID={Id:X4} -> ({X}, {Y})", objectIdRaw, objectIdMasked, x, y);

                if (_scopeManager.TryUpdateScopeObjectPosition(objectIdMasked, x, y))
                {
                    _logger.LogTrace("   -> Updated position for {Id:X4} in scope.", objectIdMasked);
                }
                else
                {
                    _logger.LogTrace("   -> Object {Id:X4} not found in scope for position update (or not tracked).", objectIdMasked);
                }

                if (objectIdMasked == _characterState.Id)
                {
                    _logger.LogInformation("🏃‍♂️ Character teleported/moved to ({X}, {Y}) via 0x15", x, y);
                    _characterState.UpdatePosition(x, y);
                    _client.UpdateConsoleTitle();
                    _client.SignalMovementHandled();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectMoved (15).");
            }
            finally
            {
                if (objectIdMasked == _characterState.Id)
                {
                    _client.SignalMovementHandledIfWalking();
                }
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0xD4, PacketRouter.NoSubCode)] // ObjectWalked
        public Task HandleObjectWalkedAsync(Memory<byte> packet)
        {
            ushort objectIdMasked = 0xFFFF;
            try
            {
                var walk = new ObjectWalked(packet);
                ushort objectIdRaw = walk.ObjectId;
                objectIdMasked = (ushort)(objectIdRaw & 0x7FFF);
                byte targetX = walk.TargetX;
                byte targetY = walk.TargetY;
                byte stepCount = walk.StepCount;
                _logger.LogDebug("🚶 Handling ObjectWalked (D4): RawID={RawId:X4}, MaskedID={Id:X4} -> ({X}, {Y}), Steps={Steps}", objectIdRaw, objectIdMasked, targetX, targetY, stepCount);

                if (_scopeManager.TryUpdateScopeObjectPosition(objectIdMasked, targetX, targetY))
                {
                    _logger.LogTrace("   -> Updated position for {Id:X4} in scope via walk packet.", objectIdMasked);
                }
                else
                {
                    _logger.LogTrace("   -> Object {Id:X4} not found in scope for walk update (or not tracked).", objectIdMasked);
                }

                if (objectIdMasked == _characterState.Id)
                {
                    _characterState.UpdatePosition(targetX, targetY);
                    _client.UpdateConsoleTitle();
                    if (stepCount == 0)
                    {
                        _logger.LogInformation("🚶‍➡️ Character walk ended/rotated at ({TargetX},{TargetY}) via 0xD4 (Steps=0)", targetX, targetY);
                        _client.SignalMovementHandled();
                    }
                    else
                    {
                        _logger.LogInformation("🚶‍➡️ Character walking -> [Server Target:({TargetX},{TargetY})] Steps:{Steps}", targetX, targetY, stepCount);
                    }
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) walking -> [Server Target:({TargetX},{TargetY})] Steps:{Steps}", objectIdMasked, targetX, targetY, stepCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectWalked (D4). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            finally
            {
                if (objectIdMasked == _characterState.Id)
                {
                    _client.SignalMovementHandledIfWalking();
                }
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x17, PacketRouter.NoSubCode)] // ObjectGotKilled
        public Task HandleObjectGotKilledAsync(Memory<byte> packet)
        {
            try
            {
                if (packet.Length < ObjectGotKilled.Length)
                {
                    _logger.LogWarning("⚠️ Received ObjectGotKilled packet (0x17) with unexpected length {Length}.", packet.Length);
                    return Task.CompletedTask;
                }
                var deathInfo = new ObjectGotKilled(packet);
                ushort killedIdRaw = deathInfo.KilledId;
                ushort killerIdRaw = deathInfo.KillerId;
                string killerName = _scopeManager.TryGetScopeObjectName(killerIdRaw, out var kn) ? (kn ?? "Unknown") : "Unknown Killer";
                string killedName = _scopeManager.TryGetScopeObjectName(killedIdRaw, out var kdn) ? (kdn ?? "Unknown Object") : "Unknown Object";
                ushort killedIdMasked = (ushort)(killedIdRaw & 0x7FFF);

                if (killedIdMasked == _characterState.Id)
                {
                    _logger.LogWarning("💀 YOU DIED! Killed by {KillerName} (ID: {KillerId:X4}).", killerName, killerIdRaw);
                    _characterState.UpdateCurrentHealthShield(0, 0);
                    _client.SignalMovementHandledIfWalking();
                    _client.UpdateConsoleTitle();
                }
                else
                {
                    _logger.LogInformation("💀 {KilledName} (ID: {KilledId:X4}) died. Killed by {KillerName} (ID: {KillerId:X4}).", killedName, killedIdRaw, killerName, killerIdRaw);
                    _scopeManager.RemoveObjectFromScope(killedIdMasked);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectGotKilled (0x17). Packet: {PacketData}", Convert.ToHexString(packet.Span));
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x18, PacketRouter.NoSubCode)] // ObjectAnimation
        public Task HandleObjectAnimationAsync(Memory<byte> packet)
        {
            try
            {
                var animation = new ObjectAnimation(packet);
                ushort objectIdRaw = animation.ObjectId;
                ushort objectIdMasked = (ushort)(objectIdRaw & 0x7FFF);
                string animDesc = animation.Animation == 0 ? "Stop" : $"Anim={animation.Animation}";
                if (objectIdMasked == _characterState.Id)
                {
                    _logger.LogInformation("🤺 Our character -> {AnimDesc}, Direction={Dir}, TargetID={Target:X4}", animDesc, animation.Direction, animation.TargetId);
                }
                else
                {
                    _logger.LogDebug("   -> Other object ({Id:X4}) -> {AnimDesc}, Direction={Dir}, TargetID={Target:X4}", objectIdMasked, animDesc, animation.Direction, animation.TargetId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing ObjectAnimation (18).");
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x65, PacketRouter.NoSubCode)] // AssignCharacterToGuild
        public Task HandleAssignCharacterToGuildAsync(Memory<byte> packet)
        {
            try
            {
                var assign = new AssignCharacterToGuild(packet);
                _logger.LogInformation("🛡️ Received AssignCharacterToGuild: {Count} players.", assign.PlayerCount);
                for (int i = 0; i < assign.PlayerCount; i++)
                {
                    var relation = assign[i];
                    ushort playerIdRaw = relation.PlayerId;
                    ushort playerIdMasked = (ushort)(playerIdRaw & 0x7FFF);
                    _logger.LogDebug("  -> Player {PlayerId:X4} (Raw: {RawId:X4}) assigned to Guild {GuildId}, Role {Role}", playerIdMasked, playerIdRaw, relation.GuildId, relation.Role);
                    // TODO: Update player's guild info in ScopeManager if needed
                    // Example: _scopeManager.UpdatePlayerGuildInfo(playerIdMasked, relation.GuildId, relation.Role);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing AssignCharacterToGuild (65).");
            }
            return Task.CompletedTask;
        }

        [PacketHandler(0x5D, PacketRouter.NoSubCode)] // GuildMemberLeftGuild
        public Task HandleGuildMemberLeftGuildAsync(Memory<byte> packet)
        {
            try
            {
                var left = new GuildMemberLeftGuild(packet);
                ushort playerIdRaw = left.PlayerId;
                ushort playerIdMasked = (ushort)(playerIdRaw & 0x7FFF);
                _logger.LogInformation("🚶 Player {PlayerId:X4} (Raw: {RawId:X4}) left guild (Is GM: {IsGM}).", playerIdMasked, playerIdRaw, left.IsGuildMaster);
                // TODO: Update player's guild info in ScopeManager if needed (e.g., set GuildId to 0)
                // Example: _scopeManager.UpdatePlayerGuildInfo(playerIdMasked, 0, GuildMemberRole.Undefined);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error parsing GuildMemberLeftGuild (5D).");
            }
            return Task.CompletedTask;
        }

        // Add other scope-related handlers here (e.g., 1F AddSummonedMonstersToScope)
    }
}