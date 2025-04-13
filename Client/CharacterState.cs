using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ServerToClient; // For SkillEntry etc.
using MuOnlineConsole.Core.Utilities; // For ItemDatabase

namespace MuOnlineConsole.Client
{
    /// <summary>
    /// Represents the state of a learned skill, including its ID, level, and display values.
    /// </summary>
    public class SkillEntryState
    {
        /// <summary>
        /// Gets or sets the unique identifier of the skill.
        /// </summary>
        public ushort SkillId { get; set; }
        /// <summary>
        /// Gets or sets the current level of the skill.
        /// </summary>
        public byte SkillLevel { get; set; }
        /// <summary>
        /// Gets or sets the current display value of the skill, if applicable.
        /// This could represent a percentage or a numerical value shown to the player.
        /// </summary>
        public float? DisplayValue { get; set; }
        /// <summary>
        /// Gets or sets the next display value of the skill, often shown in tooltips to indicate the value after leveling up.
        /// </summary>
        public float? NextDisplayValue { get; set; }
    }

    /// <summary>
    /// Holds the state of the currently logged-in character, including basic info, stats, inventory, and skills.
    /// This class is responsible for tracking and updating the character's attributes as received from the server.
    /// </summary>
    public class CharacterState
    {
        private readonly ILogger<CharacterState> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CharacterState"/> class.
        /// </summary>
        /// <param name="logger">The logger used for logging character state changes and information.</param>
        public CharacterState(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<CharacterState>();
        }

        // --- Basic Character Information ---
        /// <summary>
        /// Gets or sets the name of the character. Default is "???".
        /// </summary>
        public string Name { get; set; } = "???";
        /// <summary>
        /// Gets or sets the unique identifier of the character. Default is 0xFFFF.
        /// </summary>
        public ushort Id { get; set; } = 0xFFFF;
        /// <summary>
        /// Gets or sets a value indicating whether the character is currently in the game world.
        /// </summary>
        public bool IsInGame { get; set; } = false;
        /// <summary>
        /// Gets or sets the class number of the character. Default is Dark Wizard.
        /// </summary>
        public CharacterClassNumber Class { get; set; } = CharacterClassNumber.DarkWizard;
        /// <summary>
        /// Gets or sets the current status of the character (e.g., Normal, Poisoned).
        /// </summary>
        public CharacterStatus Status { get; set; } = CharacterStatus.Normal;
        /// <summary>
        /// Gets or sets the hero state of the character (e.g., Normal, Hero, PlayerKiller).
        /// </summary>
        public CharacterHeroState HeroState { get; set; } = CharacterHeroState.Normal;

        // --- Level and Experience Information ---
        /// <summary>
        /// Gets or sets the current level of the character. Default is 1.
        /// </summary>
        public ushort Level { get; set; } = 1;
        /// <summary>
        /// Gets or sets the current experience points of the character. Default is 0.
        /// </summary>
        public ulong Experience { get; set; } = 0;
        /// <summary>
        /// Gets or sets the experience points required to reach the next level. Default is 1.
        /// </summary>
        public ulong ExperienceForNextLevel { get; set; } = 1;
        /// <summary>
        /// Gets or sets the available level up points for stat distribution. Default is 0.
        /// </summary>
        public ushort LevelUpPoints { get; set; } = 0;
        /// <summary>
        /// Gets or sets the master level of the character. Default is 0.
        /// </summary>
        public ushort MasterLevel { get; set; } = 0; // NEW: Master Level
        /// <summary>
        /// Gets or sets the current master experience points. Default is 0.
        /// </summary>
        public ulong MasterExperience { get; set; } = 0; // NEW: Master Experience
        /// <summary>
        /// Gets or sets the master experience points required for the next master level. Default is 1.
        /// </summary>
        public ulong MasterExperienceForNextLevel { get; set; } = 1; // NEW: Master Experience for next level
        /// <summary>
        /// Gets or sets the available master level up points. Default is 0.
        /// </summary>
        public ushort MasterLevelUpPoints { get; set; } = 0; // NEW: Master Level Up Points

        // --- Position and Map Information ---
        /// <summary>
        /// Gets or sets the current X-coordinate position of the character on the map. Default is 0.
        /// </summary>
        public byte PositionX { get; set; } = 0;
        /// <summary>
        /// Gets or sets the current Y-coordinate position of the character on the map. Default is 0.
        /// </summary>
        public byte PositionY { get; set; } = 0;
        /// <summary>
        /// Gets or sets the identifier of the current map the character is in. Default is 0.
        /// </summary>
        public ushort MapId { get; set; } = 0;

        // --- Core Stats (HP, Mana, SD, AG) ---
        /// <summary>
        /// Gets or sets the current health points of the character. Default is 0.
        /// </summary>
        public uint CurrentHealth { get; set; } = 0;
        /// <summary>
        /// Gets or sets the maximum health points of the character. Default is 1.
        /// </summary>
        public uint MaximumHealth { get; set; } = 1;
        /// <summary>
        /// Gets or sets the current shield points of the character. Default is 0.
        /// </summary>
        public uint CurrentShield { get; set; } = 0;
        /// <summary>
        /// Gets or sets the maximum shield points of the character. Default is 0.
        /// </summary>
        public uint MaximumShield { get; set; } = 0;
        /// <summary>
        /// Gets or sets the current mana points of the character. Default is 0.
        /// </summary>
        public uint CurrentMana { get; set; } = 0;
        /// <summary>
        /// Gets or sets the maximum mana points of the character. Default is 1.
        /// </summary>
        public uint MaximumMana { get; set; } = 1;
        /// <summary>
        /// Gets or sets the current ability (AG) points of the character. Default is 0.
        /// </summary>
        public uint CurrentAbility { get; set; } = 0;
        /// <summary>
        /// Gets or sets the maximum ability (AG) points of the character. Default is 0.
        /// </summary>
        public uint MaximumAbility { get; set; } = 0;

        // --- Base Stats (Strength, Agility, Vitality, Energy, Leadership) ---
        /// <summary>
        /// Gets or sets the strength stat of the character. Default is 0.
        /// </summary>
        public ushort Strength { get; set; } = 0;
        /// <summary>
        /// Gets or sets the agility stat of the character. Default is 0.
        /// </summary>
        public ushort Agility { get; set; } = 0;
        /// <summary>
        /// Gets or sets the vitality stat of the character. Default is 0.
        /// </summary>
        public ushort Vitality { get; set; } = 0;
        /// <summary>
        /// Gets or sets the energy stat of the character. Default is 0.
        /// </summary>
        public ushort Energy { get; set; } = 0;
        /// <summary>
        /// Gets or sets the leadership (command) stat of the character. Default is 0.
        /// </summary>
        public ushort Leadership { get; set; } = 0;

        // --- Inventory and Money ---
        /// <summary>
        /// Concurrent dictionary to store inventory items, keyed by slot number.
        /// Uses ConcurrentDictionary for thread-safety as inventory updates might come from different threads.
        /// </summary>
        private readonly ConcurrentDictionary<byte, byte[]> _inventoryItems = new();
        /// <summary>
        /// Gets or sets the inventory expansion state. Default is 0.
        /// </summary>
        public byte InventoryExpansionState { get; set; } = 0;
        /// <summary>
        /// Gets or sets the amount of Zen in the character's inventory. Default is 0.
        /// </summary>
        public uint InventoryZen { get; set; } = 0;

        // --- Constants for Item Data Parsing (based on ItemSerializer.cs) ---
        /// <summary>
        /// Bit flag for Luck option in item option byte.
        /// </summary>
        private const byte LuckFlagBit = 4; // Bit 2
        /// <summary>
        /// Bit flag for Skill option in item option byte.
        /// </summary>
        private const byte SkillFlagBit = 128; // Bit 7
        /// <summary>
        /// Mask to extract item level from item option byte.
        /// </summary>
        private const byte LevelMask = 0x78; // Bits 3-6
        /// <summary>
        /// Bit shift value to get item level from masked bits.
        /// </summary>
        private const byte LevelShift = 3;
        /// <summary>
        /// Mask to extract option level (bits 0-1) from item option byte.
        /// </summary>
        private const byte OptionLevelMask = 0x03; // Bits 0-1
        /// <summary>
        /// Bit shift for the 3rd option level bit located in the Excellent Option byte.
        /// </summary>
        private const byte Option3rdBitShift = 4; // For the 3rd option level bit in ExcByte
        /// <summary>
        /// Mask for the 3rd option level bit located in the Excellent Option byte.
        /// </summary>
        private const byte Option3rdBitMask = 0x40; // The 3rd option level bit in ExcByte

        /// <summary>
        /// Flag for Guardian option (Level 380 option) in item group byte.
        /// </summary>
        private const byte GuardianOptionFlag = 0x08; // In Byte 5

        /// <summary>
        /// Mask for Ancient Bonus Level in Ancient byte.
        /// </summary>
        private const byte AncientBonusLevelMask = 0b1100; // Bits 2-3 in AncientByte
        /// <summary>
        /// Bit shift for Ancient Bonus Level.
        /// </summary>
        private const byte AncientBonusLevelShift = 2;
        /// <summary>
        /// Mask for Ancient Set Discriminator in Ancient byte.
        /// </summary>
        private const byte AncientDiscriminatorMask = 0b0011; // Bits 0-1 in AncientByte

        // --- Excellent Option Bits (in ExcByte, index 3) ---
        /// <summary>
        /// Excellent option bit for Mana Increase.
        /// </summary>
        private const byte ExcManaInc = 0b0000_0001; // Bit 0
        /// <summary>
        /// Excellent option bit for Life Increase.
        /// </summary>
        private const byte ExcLifeInc = 0b0000_0010; // Bit 1
        /// <summary>
        /// Excellent option bit for Damage Increase.
        /// </summary>
        private const byte ExcDmgInc = 0b0000_0100; // Bit 2 - Note: Serializer uses different bit order, client usually sees this order
        /// <summary>
        /// Excellent option bit for Attack Speed Increase.
        /// </summary>
        private const byte ExcSpeedInc = 0b0000_1000; // Bit 3
        /// <summary>
        /// Excellent option bit for Damage Rate Increase.
        /// </summary>
        private const byte ExcRateInc = 0b0001_0000; // Bit 4
        /// <summary>
        /// Excellent option bit for Zen Increase.
        /// </summary>
        private const byte ExcZenInc = 0b0010_0000; // Bit 5
        // Bit 6 is the 3rd Option Level bit
        // Bit 7 is the Item Group high bit indicator

        // --- Skills ---
        /// <summary>
        /// Concurrent dictionary to store character skills, keyed by skill ID.
        /// </summary>
        private readonly ConcurrentDictionary<ushort, SkillEntryState> _skillList = new(); // NEW: Store skills by ID

        // --- Update Methods ---

        /// <summary>
        /// Updates the character's position coordinates.
        /// </summary>
        /// <param name="x">The new X-coordinate.</param>
        /// <param name="y">The new Y-coordinate.</param>
        public void UpdatePosition(byte x, byte y)
        {
            PositionX = x;
            PositionY = y;
            _logger.LogDebug("Character position updated to X: {X}, Y: {Y}", x, y);
        }

        /// <summary>
        /// Updates the character's current map ID.
        /// </summary>
        /// <param name="mapId">The new map ID.</param>
        public void UpdateMap(ushort mapId)
        {
            MapId = mapId;
            _logger.LogInformation("Character map changed to ID: {MapId}", mapId);
            // Consider clearing scope here if map changes significantly
        }

        /// <summary>
        /// Updates the character's level, experience, and level up points.
        /// </summary>
        /// <param name="level">The current level.</param>
        /// <param name="currentExperience">The current experience points.</param>
        /// <param name="nextLevelExperience">The experience points required for the next level.</param>
        /// <param name="levelUpPoints">The available level up points.</param>
        public void UpdateLevelAndExperience(ushort level, ulong currentExperience, ulong nextLevelExperience, ushort levelUpPoints)
        {
            Level = level;
            Experience = currentExperience;
            ExperienceForNextLevel = Math.Max(1, nextLevelExperience); // Ensure > 0 to avoid division by zero
            LevelUpPoints = levelUpPoints;
            _logger.LogInformation("Level and experience updated. Level: {Level}, Exp: {Experience}, Next Level Exp: {NextLevelExperience}, LevelUpPoints: {LevelUpPoints}", level, currentExperience, nextLevelExperience, levelUpPoints);
        }

        /// <summary>
        /// Updates the character's master level, master experience, and master level up points.
        /// </summary>
        /// <param name="masterLevel">The current master level.</param>
        /// <param name="currentMasterExperience">The current master experience points.</param>
        /// <param name="nextMasterLevelExperience">The master experience points required for the next master level.</param>
        /// <param name="masterLevelUpPoints">The available master level up points.</param>
        public void UpdateMasterLevelAndExperience(ushort masterLevel, ulong currentMasterExperience, ulong nextMasterLevelExperience, ushort masterLevelUpPoints) // NEW
        {
            MasterLevel = masterLevel;
            MasterExperience = currentMasterExperience;
            MasterExperienceForNextLevel = Math.Max(1, nextMasterLevelExperience);
            MasterLevelUpPoints = masterLevelUpPoints;
            _logger.LogInformation("Master level and experience updated. Master Level: {MasterLevel}, Master Exp: {MasterExperience}, Next Master Level Exp: {NextMasterLevelExperience}, Master LevelUpPoints: {MasterLevelUpPoints}", masterLevel, currentMasterExperience, nextMasterLevelExperience, masterLevelUpPoints);
        }

        /// <summary>
        /// Adds experience points to the character's current experience.
        /// </summary>
        /// <param name="addedExperience">The amount of experience points to add.</param>
        public void AddExperience(uint addedExperience)
        {
            // Note: This is a simplified update. A full implementation
            // would need to handle level ups based on ExperienceForNextLevel.
            // The server usually sends a LevelUp packet anyway.
            Experience += addedExperience;
            _logger.LogDebug("Added experience: {AddedExperience}. Total Experience: {Experience}", addedExperience, Experience);
        }

        /// <summary>
        /// Updates the character's current health and shield points.
        /// </summary>
        /// <param name="currentHealth">The current health points.</param>
        /// <param name="currentShield">The current shield points.</param>
        public void UpdateCurrentHealthShield(uint currentHealth, uint currentShield)
        {
            CurrentHealth = currentHealth;
            CurrentShield = currentShield;
            _logger.LogInformation("❤️ HP: {CurrentHealth}/{MaximumHealth} | 🛡️ SD: {CurrentShield}/{MaximumShield}",
                CurrentHealth, MaximumHealth, CurrentShield, MaximumShield);
        }

        /// <summary>
        /// Updates the character's maximum health and shield points.
        /// </summary>
        /// <param name="maximumHealth">The maximum health points.</param>
        /// <param name="maximumShield">The maximum shield points.</param>
        public void UpdateMaximumHealthShield(uint maximumHealth, uint maximumShield)
        {
            MaximumHealth = Math.Max(1, maximumHealth);
            MaximumShield = maximumShield;
            _logger.LogInformation("❤️ Max HP: {MaximumHealth} | 🛡️ Max SD: {MaximumShield}",
                MaximumHealth, MaximumShield);
        }

        /// <summary>
        /// Updates the character's current mana and ability points.
        /// </summary>
        /// <param name="currentMana">The current mana points.</param>
        /// <param name="currentAbility">The current ability points.</param>
        public void UpdateCurrentManaAbility(uint currentMana, uint currentAbility)
        {
            CurrentMana = currentMana;
            CurrentAbility = currentAbility;
            _logger.LogInformation("💧 Mana: {CurrentMana}/{MaximumMana} | ✨ AG: {CurrentAbility}/{MaximumAbility}",
                CurrentMana, MaximumMana, CurrentAbility, MaximumAbility);
        }

        /// <summary>
        /// Updates the character's maximum mana and ability points.
        /// </summary>
        /// <param name="maximumMana">The maximum mana points.</param>
        /// <param name="maximumAbility">The maximum ability points.</param>
        public void UpdateMaximumManaAbility(uint maximumMana, uint maximumAbility)
        {
            MaximumMana = Math.Max(1, maximumMana);
            MaximumAbility = maximumAbility;
            _logger.LogInformation("💧 Max Mana: {MaximumMana} | ✨ Max AG: {MaximumAbility}",
                MaximumMana, MaximumAbility);
        }

        /// <summary>
        /// Updates the character's base stats (Strength, Agility, Vitality, Energy, Leadership).
        /// </summary>
        /// <param name="strength">The strength stat.</param>
        /// <param name="agility">The agility stat.</param>
        /// <param name="vitality">The vitality stat.</param>
        /// <param name="energy">The energy stat.</param>
        /// <param name="leadership">The leadership stat.</param>
        public void UpdateStats(ushort strength, ushort agility, ushort vitality, ushort energy, ushort leadership)
        {
            Strength = strength;
            Agility = agility;
            Vitality = vitality;
            Energy = energy;
            Leadership = leadership;
            _logger.LogInformation("📊 Stats: Str={Str}, Agi={Agi}, Vit={Vit}, Ene={Ene}, Cmd={Cmd}",
                Strength, Agility, Vitality, Energy, Leadership);
        }

        /// <summary>
        /// Increments a specific character stat attribute by a given amount.
        /// </summary>
        /// <param name="attribute">The stat attribute to increment.</param>
        /// <param name="amount">The amount to increment by (default is 1).</param>
        public void IncrementStat(CharacterStatAttribute attribute, ushort amount = 1) // NEW: Helper to increment base stats
        {
            switch (attribute)
            {
                case CharacterStatAttribute.Strength:
                    Strength += amount;
                    break;
                case CharacterStatAttribute.Agility:
                    Agility += amount;
                    break;
                case CharacterStatAttribute.Vitality:
                    Vitality += amount;
                    break;
                case CharacterStatAttribute.Energy:
                    Energy += amount;
                    break;
                case CharacterStatAttribute.Leadership:
                    Leadership += amount;
                    break;
            }
            _logger.LogDebug("Incremented stat {Attribute} by {Amount}", attribute, amount);
        }

        /// <summary>
        /// Updates the amount of Zen in the character's inventory.
        /// </summary>
        /// <param name="zen">The new Zen amount.</param>
        public void UpdateInventoryZen(uint zen)
        {
            InventoryZen = zen;
            _logger.LogDebug("Inventory Zen updated to: {Zen}", zen);
        }

        /// <summary>
        /// Updates the character's status and hero state.
        /// </summary>
        /// <param name="status">The character status.</param>
        /// <param name="heroState">The character hero state.</param>
        public void UpdateStatus(CharacterStatus status, CharacterHeroState heroState)
        {
            Status = status;
            HeroState = heroState;
            _logger.LogInformation("Character status updated. Status: {Status}, Hero State: {HeroState}", status, heroState);
        }

        /// <summary>
        /// Clears all items from the character's inventory.
        /// </summary>
        public void ClearInventory()
        {
            _inventoryItems.Clear();
            _logger.LogDebug("Inventory cleared.");
        }

        /// <summary>
        /// Adds or updates an item in the character's inventory at a specific slot.
        /// </summary>
        /// <param name="slot">The inventory slot number.</param>
        /// <param name="itemData">The byte array representing the item data.</param>
        public void AddOrUpdateInventoryItem(byte slot, byte[] itemData)
        {
            _inventoryItems[slot] = itemData;
            string itemName = ItemDatabase.GetItemName(itemData) ?? "Unknown Item";
            _logger.LogDebug("Inventory item added/updated at slot {Slot}: {ItemName}", slot, itemName);
        }

        /// <summary>
        /// Removes an item from the character's inventory at a specific slot.
        /// </summary>
        /// <param name="slot">The inventory slot number to remove the item from.</param>
        public void RemoveInventoryItem(byte slot)
        {
            bool removed = _inventoryItems.TryRemove(slot, out _);
            if (removed)
            {
                _logger.LogDebug("Inventory item removed from slot {Slot}", slot);
            }
            else
            {
                _logger.LogWarning("Attempted to remove inventory item from slot {Slot}, but no item found.", slot);
            }
        }

        /// <summary>
        /// Updates the durability of an item in the inventory at a specific slot.
        /// </summary>
        /// <param name="slot">The inventory slot number.</param>
        /// <param name="durability">The new durability value.</param>
        public void UpdateItemDurability(byte slot, byte durability) // NEW
        {
            if (_inventoryItems.TryGetValue(slot, out byte[]? itemData))
            {
                // Assuming durability is at a fixed index (e.g., index 2 for many versions)
                // Adjust index if necessary based on specific item data structure used by the server/protocol version.
                const int durabilityIndex = 2;
                if (itemData.Length > durabilityIndex)
                {
                    itemData[durabilityIndex] = durability;
                    _logger.LogDebug("Item durability updated for slot {Slot} to {Durability}", slot, durability);
                }
                else
                {
                    _logger.LogWarning("Could not update item durability for slot {Slot}, item data too short.", slot);
                }
            }
            else
            {
                _logger.LogWarning("Could not update item durability for slot {Slot}, item not found.", slot);
            }
        }

        // --- Skill List Methods --- NEW Section
        /// <summary>
        /// Clears the character's skill list.
        /// </summary>
        public void ClearSkillList()
        {
            _skillList.Clear();
            _logger.LogDebug("Skill list cleared.");
        }

        /// <summary>
        /// Adds or updates a skill in the character's skill list.
        /// </summary>
        /// <param name="skill">The SkillEntryState object representing the skill.</param>
        public void AddOrUpdateSkill(SkillEntryState skill)
        {
            _skillList[skill.SkillId] = skill;
            _logger.LogDebug("Skill added/updated. Skill ID: {SkillId}, Level: {SkillLevel}", skill.SkillId, skill.SkillLevel);
        }

        /// <summary>
        /// Removes a skill from the character's skill list by its skill ID.
        /// </summary>
        /// <param name="skillId">The ID of the skill to remove.</param>
        public void RemoveSkill(ushort skillId)
        {
            bool removed = _skillList.TryRemove(skillId, out _);
            if (removed)
            {
                _logger.LogDebug("Skill removed. Skill ID: {SkillId}", skillId);
            }
            else
            {
                _logger.LogWarning("Attempted to remove skill with ID {SkillId}, but skill not found.", skillId);
            }
        }

        /// <summary>
        /// Gets all skills in the skill list as an enumerable collection, ordered by skill ID.
        /// </summary>
        /// <returns>An enumerable collection of SkillEntryState objects.</returns>
        public IEnumerable<SkillEntryState> GetSkills()
        {
            return _skillList.Values.OrderBy(s => s.SkillId);
        }
        // --- End Skill List Methods ---

        // --- Display Methods ---

        /// <summary>
        /// Gets a formatted string representation of the character's inventory.
        /// </summary>
        /// <returns>A string representing the inventory details.</returns>
        public string GetInventoryDisplay()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- Inventory ---");
            sb.AppendLine($"  Zen: {InventoryZen:N0}");
            if (_inventoryItems.IsEmpty)
            {
                sb.AppendLine(" (Inventory is empty)");
            }
            else
            {
                foreach (var kvp in _inventoryItems.OrderBy(kv => kv.Key))
                {
                    byte slot = kvp.Key;
                    byte[] itemData = kvp.Value;
                    string itemName = ItemDatabase.GetItemName(itemData) ?? "Unknown Item";
                    string itemDetails = ParseItemDetails(itemData); // NEW: Get detailed info
                    sb.AppendLine($"  Slot {slot,3}: {itemName}{itemDetails}"); // Append details
                }
            }
            sb.AppendLine($"  Expansion State: {InventoryExpansionState}");
            sb.AppendLine("-----------------\n");
            return sb.ToString();
        }

        /// <summary>
        /// Gets a formatted string representation of the character's stats.
        /// </summary>
        /// <returns>A string representing the character stats details.</returns>
        public string GetStatsDisplay()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- Character Stats ---");
            sb.AppendLine($"  Name: {Name} (ID: {Id:X4})");
            sb.AppendLine($"  Class: {CharacterClassDatabase.GetClassName(Class)}"); // Use Database for class name
            sb.AppendLine($"  Level: {Level} ({LevelUpPoints} Points)");
            if (MasterLevel > 0)
            {
                sb.AppendLine($"  Master Level: {MasterLevel} ({MasterLevelUpPoints} Points)");
            }
            sb.AppendLine($"  Exp: {Experience:N0} / {ExperienceForNextLevel:N0}");
            if (MasterLevel > 0)
            {
                sb.AppendLine($"  M.Exp: {MasterExperience:N0} / {MasterExperienceForNextLevel:N0}");
            }
            sb.AppendLine($"  Map: {MapId} ({PositionX}, {PositionY})");
            sb.AppendLine($"  Status: {Status}, Hero State: {HeroState}");
            sb.AppendLine($"  HP: {CurrentHealth}/{MaximumHealth}");
            sb.AppendLine($"  Mana: {CurrentMana}/{MaximumMana}");
            sb.AppendLine($"  SD: {CurrentShield}/{MaximumShield}");
            sb.AppendLine($"  AG: {CurrentAbility}/{MaximumAbility}");
            sb.AppendLine($"  Strength: {Strength}");
            sb.AppendLine($"  Agility: {Agility}");
            sb.AppendLine($"  Vitality: {Vitality}");
            sb.AppendLine($"  Energy: {Energy}");
            sb.AppendLine($"  Command: {Leadership}");
            sb.AppendLine("-----------------------\n");
            return sb.ToString();
        }

        /// <summary>
        /// Gets a formatted string representation of the character's skill list.
        /// </summary>
        /// <returns>A string representing the skill list details.</returns>
        public string GetSkillListDisplay()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- Skill List ---");
            if (_skillList.IsEmpty)
            {
                sb.AppendLine(" (No skills learned/equipped)");
            }
            else
            {
                foreach (var skill in GetSkills())
                {
                    // TODO: Look up skill name based on SkillId if a database is available
                    sb.Append($"  ID: {skill.SkillId,-5} Level: {skill.SkillLevel,-2}");
                    if (skill.DisplayValue.HasValue)
                    {
                        sb.Append($" Value: {skill.DisplayValue.Value:F1}");
                    }
                    if (skill.NextDisplayValue.HasValue)
                    {
                        sb.Append($" Next: {skill.NextDisplayValue.Value:F1}");
                    }
                    sb.AppendLine();
                }
            }
            sb.AppendLine("------------------\n");
            return sb.ToString();
        }

        // --- Item Data Parsing Helpers --- (UPDATED Section)

        /// <summary>
        /// Parses item data bytes based on ItemSerializer logic (Season 6 assumed).
        /// Extracts item level, skill, luck, option, excellent options, ancient options, level 380 option, harmony option, socket bonus, and socket options.
        /// </summary>
        /// <param name="itemData">Byte array of item data.</param>
        /// <returns>String containing parsed item details, or an empty string if no details are parsed.</returns>
        private string ParseItemDetails(byte[] itemData)
        {
            if (itemData == null)
            {
                return " (Null Data)";
            }
            int dataLength = itemData.Length;
            if (dataLength < 3)
            {
                return " (Data Too Short)"; // Need at least ID, Opt/Lvl, Dur
            }

            var details = new StringBuilder();
            byte optionLevelByte = itemData[1];
            byte durability = itemData[2];
            byte excByte = dataLength > 3 ? itemData[3] : (byte)0; // Excellent options AND 3rd option bit AND high group bit
            byte ancientByte = dataLength > 4 ? itemData[4] : (byte)0;
            byte groupByte = dataLength > 5 ? itemData[5] : (byte)0; // Item Group high nibble + 380 opt flag
            byte harmonyByte = dataLength > 6 ? itemData[6] : (byte)0; // Harmony Option Type + Socket Bonus Type
            byte harmonyLevelByte = dataLength > 7 ? itemData[7] : (byte)0; // Harmony Option Level

            // --- Extract Level ---
            int level = (optionLevelByte & LevelMask) >> LevelShift;
            if (level > 0)
            {
                details.Append($" +{level}");
            }

            // --- Extract Skill ---
            bool hasSkill = (optionLevelByte & SkillFlagBit) != 0;
            if (hasSkill)
            {
                details.Append(" +Skill");
            }

            // --- Extract Luck ---
            bool hasLuck = (optionLevelByte & LuckFlagBit) != 0;
            if (hasLuck)
            {
                details.Append(" +Luck");
            }

            // --- Extract Option ---
            // Option level uses bits 0, 1 from OptionLevelByte and bit 6 from ExcByte
            int optionLevel = (optionLevelByte & OptionLevelMask); // Get bits 0, 1
            if ((excByte & Option3rdBitMask) != 0) // Check bit 6 of ExcByte
            {
                optionLevel |= 0b100; // Add the 3rd bit (value 4)
            }
            if (optionLevel > 0)
            {
                details.Append($" +{optionLevel * 4} Opt"); // Display as +4, +8, ... +28
            }

            // --- Extract Excellent Options (from ExcByte, index 3) ---
            // Use only bits 0-5 for excellent options
            var excellentOptions = ParseExcellentOptions(excByte);
            if (excellentOptions.Any())
            {
                details.Append($" +Exc({string.Join(",", excellentOptions)})");
            }

            // --- Extract Ancient Options (Byte 4) ---
            if ((ancientByte & 0x0F) > 0) // Check if any ancient bits are set
            {
                int setId = ancientByte & AncientDiscriminatorMask;
                int bonusLevel = (ancientByte & AncientBonusLevelMask) >> AncientBonusLevelShift;
                details.Append($" +Anc(Set:{setId}");
                if (bonusLevel > 0)
                {
                    details.Append($",Lvl:{bonusLevel}"); // Bonus level 1=+5, 2=+10
                }
                details.Append(")");
            }

            // --- Extract Level 380 Option (PvP Option) (from GroupByte, index 5) ---
            bool has380Option = (groupByte & GuardianOptionFlag) != 0;
            if (has380Option)
            {
                details.Append(" +PvP"); // Or "+380 Opt"
            }

            // --- Extract Harmony Option (Byte 6, 7) ---
            byte harmonyType = (byte)(harmonyByte & 0xF0); // Harmony type is often in high nibble
            byte harmonyLevel = harmonyLevelByte;
            if (harmonyType > 0)
            {
                // TODO: Map harmonyType to actual option name
                details.Append($" +Har(T:{harmonyType >> 4},L:{harmonyLevel})");
            }

            // --- Extract Socket Bonus Option (from HarmonyByte, index 6) ---
            byte socketBonusType = (byte)(harmonyByte & 0x0F); // Socket bonus often in low nibble
            if (socketBonusType > 0)
            {
                // TODO: Map socketBonusType to actual option name
                details.Append($" +SockBonus({socketBonusType})");
            }

            // --- Extract Socket Options (Bytes 7, 8, 9, 10, 11 for S6+) ---
            // Note: Indices shift because Harmony Level is now byte 7
            if (dataLength > 11)
            {
                byte socket1 = dataLength > 7 ? itemData[7] : (byte)0xFF;
                byte socket2 = dataLength > 8 ? itemData[8] : (byte)0xFF;
                byte socket3 = dataLength > 9 ? itemData[9] : (byte)0xFF;
                byte socket4 = dataLength > 10 ? itemData[10] : (byte)0xFF;
                byte socket5 = dataLength > 11 ? itemData[11] : (byte)0xFF;

                var sockets = new List<string>();
                if (socket1 != 0xFF && socket1 != 0xFE)
                {
                    sockets.Add(ParseSocketOption(socket1)); // 0xFF=Empty, 0xFE=No Socket
                }
                if (socket2 != 0xFF && socket2 != 0xFE)
                {
                    sockets.Add(ParseSocketOption(socket2));
                }
                if (socket3 != 0xFF && socket3 != 0xFE)
                {
                    sockets.Add(ParseSocketOption(socket3));
                }
                if (socket4 != 0xFF && socket4 != 0xFE)
                {
                    sockets.Add(ParseSocketOption(socket4));
                }
                if (socket5 != 0xFF && socket5 != 0xFE)
                {
                    sockets.Add(ParseSocketOption(socket5));
                }

                if (sockets.Any())
                {
                    details.Append($" +Socket({string.Join(",", sockets)})");
                }
            }

            // --- Add Durability ---
            details.Append($" (Dur: {durability})");

            // --- Final Formatting ---
            string result = details.ToString().Trim();
            return string.IsNullOrEmpty(result) ? string.Empty : $" {result}";
        }

        /// <summary>
        /// Parses the excellent options byte (byte 3 in S6 structure) to extract enabled excellent options.
        /// </summary>
        /// <param name="excByte">The excellent options byte.</param>
        /// <returns>A list of strings representing the enabled excellent options.</returns>
        private List<string> ParseExcellentOptions(byte excByte)
        {
            var options = new List<string>();
            // Check bits 0-5 based on common client display order
            if ((excByte & ExcManaInc) != 0)
            {
                options.Add("MP/8"); // Mana Increase
            }
            if ((excByte & ExcLifeInc) != 0)
            {
                options.Add("HP/8"); // Life Increase
            }
            if ((excByte & ExcSpeedInc) != 0)
            {
                options.Add("Speed"); // Attack Speed +7
            }
            if ((excByte & ExcDmgInc) != 0)
            {
                options.Add("Dmg%"); // Damage Increase +2%
            }
            if ((excByte & ExcRateInc) != 0)
            {
                options.Add("Rate"); // Damage Increase +Lvl/20
            }
            if ((excByte & ExcZenInc) != 0)
            {
                options.Add("Zen"); // Zen Increase +40%
            }

            return options;
        }

        /// <summary>
        /// Parses a socket option byte into a readable string (placeholder).
        /// </summary>
        /// <param name="socketByte">The socket option byte.</param>
        /// <returns>A string representing the socket option (placeholder).</returns>
        private string ParseSocketOption(byte socketByte)
        {
            // TODO: Implement mapping from socketByte value to actual Seed Sphere name/effect
            // This requires knowing the specific values assigned to each Seed Sphere type.
            // Example:
            // return socketByte switch {
            //     0 => "Fire(AtkLvl)", 1 => "Fire(Spd)", ... , 10 => "Water(Block)", ... , 254 => "Empty", _ => $"Raw({socketByte})"
            // };
            return $"S:{socketByte}"; // Placeholder
        }

        // --- End Item Data Parsing Helpers ---
    }
}