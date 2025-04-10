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
        public ushort Id { get; init; } // This will now store the MASKED ID (used as key)
        public ushort RawId { get; init; } // Add this to store the original Raw ID
        public byte PositionX { get; set; }
        public byte PositionY { get; set; }
        public abstract ScopeObjectType ObjectType { get; }
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

        // Modified constructor to accept both IDs
        protected ScopeObject(ushort maskedId, ushort rawId, byte x, byte y)
        {
            Id = maskedId;
            RawId = rawId; // Store the raw ID
            PositionX = x;
            PositionY = y;
        }

        public override string ToString()
        {
            // Display both IDs for clarity during debugging/listing
            return $"ID: {Id:X4} (Raw: {RawId:X4}) ({ObjectType}) at [{PositionX},{PositionY}]";
        }
    }

    public class PlayerScopeObject : ScopeObject
    {
        public string Name { get; set; }
        public override ScopeObjectType ObjectType => ScopeObjectType.Player;

        // Modified constructor
        public PlayerScopeObject(ushort maskedId, ushort rawId, byte x, byte y, string name)
            : base(maskedId, rawId, x, y)
        {
            Name = name;
        }

        public override string ToString()
        {
            // Display both IDs
            return $"ID: {Id:X4} (Raw: {RawId:X4}) (Player: {Name}) at [{PositionX},{PositionY}]";
        }
    }

    public class NpcScopeObject : ScopeObject // Also used for Monsters for simplicity now
    {
        public string? Name { get; set; } // Some versions have names
        public ushort TypeNumber { get; set; } // All versions should have this
        public override ScopeObjectType ObjectType => ScopeObjectType.Npc; // Or Monster based on TypeNumber range?

        // Modified constructor
        public NpcScopeObject(ushort maskedId, ushort rawId, byte x, byte y, ushort typeNumber, string? name = null)
            : base(maskedId, rawId, x, y)
        {
            TypeNumber = typeNumber;
            Name = name;
        }

        public override string ToString()
        {
            string identifier = string.IsNullOrWhiteSpace(Name) ? $"Type {TypeNumber}" : Name;
            // Display both IDs
            return $"ID: {Id:X4} (Raw: {RawId:X4}) (NPC: {identifier}) at [{PositionX},{PositionY}]";
        }
    }

    public class ItemScopeObject : ScopeObject
    {
        public string ItemDescription { get; set; }
        public ReadOnlyMemory<byte> ItemData { get; } // Store original data
        public override ScopeObjectType ObjectType => ScopeObjectType.Item;

        // Modified constructor
        public ItemScopeObject(ushort maskedId, ushort rawId, byte x, byte y, ReadOnlySpan<byte> itemData)
            : base(maskedId, rawId, x, y)
        {
            ItemData = itemData.ToArray(); // Store a copy

            // Parsing logic (keep as is or improve)
            ItemDescription = $"Item Data (Len:{ItemData.Length})"; // Placeholder
            if (ItemData.Length > 0)
            {
                ItemDescription = ItemDatabase.GetItemName(ItemData.Span) ?? $"Unknown (Data: {Convert.ToHexString(ItemData.Span)})";
            }
        }

        public override string ToString()
        {
            // Display both IDs
            return $"ID: {Id:X4} (Raw: {RawId:X4}) (Item: {ItemDescription}) at [{PositionX},{PositionY}]";
        }
    }

    public class MoneyScopeObject : ScopeObject
    {
        public uint Amount { get; set; }
        public override ScopeObjectType ObjectType => ScopeObjectType.Money;

        // Modified constructor
        public MoneyScopeObject(ushort maskedId, ushort rawId, byte x, byte y, uint amount)
            : base(maskedId, rawId, x, y)
        {
            Amount = amount;
        }

        public override string ToString()
        {
            // Display both IDs
            return $"ID: {Id:X4} (Raw: {RawId:X4}) (Money: {Amount}) at [{PositionX},{PositionY}]";
        }
    }
}