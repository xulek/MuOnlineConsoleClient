// ScopeObject.cs
using System.Text;
using MUnique.OpenMU.Network.Packets; // For CharacterHeroState if needed later

namespace MuOnlineConsole
{
    public enum ScopeObjectType
    {
        Player,
        Npc,
        Monster, // Can be treated similar to NPC often
        Item,
        Money
    }

    public abstract class ScopeObject
    {
        public ushort Id { get; init; }
        public byte PositionX { get; set; }
        public byte PositionY { get; set; }
        public abstract ScopeObjectType ObjectType { get; }
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

        protected ScopeObject(ushort id, byte x, byte y)
        {
            Id = id;
            PositionX = x;
            PositionY = y;
        }

        public override string ToString()
        {
            return $"ID: {Id:X4} ({ObjectType}) at [{PositionX},{PositionY}]";
        }
    }

    public class PlayerScopeObject : ScopeObject
    {
        public string Name { get; set; }
        public override ScopeObjectType ObjectType => ScopeObjectType.Player;

        public PlayerScopeObject(ushort id, byte x, byte y, string name) : base(id, x, y)
        {
            Name = name;
        }

        public override string ToString()
        {
            return $"ID: {Id:X4} (Player: {Name}) at [{PositionX},{PositionY}]";
        }
    }

    public class NpcScopeObject : ScopeObject // Also used for Monsters for simplicity now
    {
        public string? Name { get; set; } // Some versions have names
        public ushort TypeNumber { get; set; } // All versions should have this
        public override ScopeObjectType ObjectType => ScopeObjectType.Npc; // Or Monster based on TypeNumber range?

        public NpcScopeObject(ushort id, byte x, byte y, ushort typeNumber, string? name = null) : base(id, x, y)
        {
            TypeNumber = typeNumber;
            Name = name;
        }

        public override string ToString()
        {
            string identifier = string.IsNullOrWhiteSpace(Name) ? $"Type {TypeNumber}" : Name;
            return $"ID: {Id:X4} (NPC: {identifier}) at [{PositionX},{PositionY}]";
        }
    }

    public class ItemScopeObject : ScopeObject
    {
        // TODO: Implement proper item parsing later
        public string ItemDescription { get; set; }
        public override ScopeObjectType ObjectType => ScopeObjectType.Item;

        public ItemScopeObject(ushort id, byte x, byte y, ReadOnlySpan<byte> itemData) : base(id, x, y)
        {
            // Very basic parsing - replace with proper item library usage later
            ItemDescription = $"Item Data (Len:{itemData.Length})"; // Placeholder
            if (itemData.Length > 0)
            {
                // Example: Try to get item group/index if possible (adjust based on actual itemData format)
                // This is highly dependent on the protocol version and item structure
                // For now, just show the first byte as hex
                ItemDescription = $"Item (First Byte: {itemData[0]:X2})";
            }
        }

        public override string ToString()
        {
            return $"ID: {Id:X4} (Item: {ItemDescription}) at [{PositionX},{PositionY}]";
        }
    }

    public class MoneyScopeObject : ScopeObject
    {
        public uint Amount { get; set; }
        public override ScopeObjectType ObjectType => ScopeObjectType.Money;

        public MoneyScopeObject(ushort id, byte x, byte y, uint amount) : base(id, x, y)
        {
            Amount = amount;
        }

        public override string ToString()
        {
            return $"ID: {Id:X4} (Money: {Amount}) at [{PositionX},{PositionY}]";
        }
    }
}