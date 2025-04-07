using MUnique.OpenMU.Network.Packets.ConnectServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;

namespace MuOnlineConsole
{
    /// <summary>
    /// Holds Game Server packet codes which are known to have sub-codes.
    /// </summary>
    public static class SubCodeHolder
    {
        // Based on ServerToClientPackets.cs from MUnique.OpenMU.Network
        private static readonly HashSet<byte> CodesWithSubCode = new HashSet<byte>
        {
            LoginResponse.Code, // F1
            CharacterList.Code, // F3
            NpcItemBuyFailed.Code, // 32 - Note: ItemBought uses 32 without subcode
            TradeMoneySetResponse.Code, // 3A
            PlayerShopSetItemPriceResponse.Code, // 3F
            CurrentHealthAndShield.Code, // 26
            CurrentManaAndAbility.Code, // 27
            LegacyQuestMonsterKillInfo.Code, // A4
            DuelStartResult.Code, // AA
            ChaosCastleEnterResult.Code, // AF
            IllusionTempleEnterResult.Code, // BF
            QuestEventResponse.Code, // F6
            OpenNpcDialog.Code, // F9
            CharacterClassCreationUnlock.Code, // DE
            MasterCharacterLevelUpdate.Code, // F3
            MasterSkillLevelUpdate.Code, // F3
            MasterSkillList.Code, // F3
            MapChanged.Code, // 1C
            // Add any other codes from ServerToClientPackets.cs which have a SubCode field
        };

        // Explicitly add codes if they are not covered by the above structs
        static SubCodeHolder()
        {
            // Redundant if structs cover them, but safe to keep
            CodesWithSubCode.Add(0xF1); // Login/Logout
            CodesWithSubCode.Add(0xF3); // Character List/Info/Level/Stat/Skill/Master
            CodesWithSubCode.Add(0x32); // Buy Item Result (Failed uses FF)
            CodesWithSubCode.Add(0x3A); // Trade Money Set Response
            CodesWithSubCode.Add(0x3F); // Player Shop Price Set Result / Item List / Close / Sold / Buy Result
            CodesWithSubCode.Add(0x26); // Health/Shield Update
            CodesWithSubCode.Add(0x27); // Mana/Ability Update
            CodesWithSubCode.Add(0xA3); // Legacy Quest Reward (Not in provided structs, assuming it might have subcodes)
            CodesWithSubCode.Add(0xA4); // Legacy Quest Monster Kill Info
            CodesWithSubCode.Add(0xAA); // Duel Start/End/Score/Health/Init/Spectator
            CodesWithSubCode.Add(0xAF); // Chaos Castle Enter Result / Position Set
            CodesWithSubCode.Add(0xB2); // Castle Siege Register/Unregister/State/Mark/Defense/Tax
            CodesWithSubCode.Add(0xB3); // Castle Siege Gate/Statue List
            CodesWithSubCode.Add(0xB7); // Castle Siege Catapult/Weapon
            CodesWithSubCode.Add(0xB9); // Castle Siege Owner Logo/Hunting Zone
            CodesWithSubCode.Add(0xBC); // Lahap Mix Request (Server doesn't send this code)
            CodesWithSubCode.Add(0xBD); // Crywolf Info/Contract/Benefit
            CodesWithSubCode.Add(0xBF); // Illusion Temple Enter/State/Skill/Result/Reward / Lucky Coin / Doppelganger
            CodesWithSubCode.Add(0xC1); // Friend Add/Delete Response
            CodesWithSubCode.Add(0xC3); // Friend Request Response (Not in provided structs)
            CodesWithSubCode.Add(0xC8); // Letter Delete Response (Not in provided structs)
            CodesWithSubCode.Add(0xCA); // Chat Room Invite Response (Not in provided structs)
            CodesWithSubCode.Add(0xCB); // Friend Invite Result
            CodesWithSubCode.Add(0xD0); // Special NPC Action Results
            CodesWithSubCode.Add(0xD1); // Kanturu/Raklion Info/Enter
            CodesWithSubCode.Add(0xD2); // Cash Shop Point/Open/Buy/Gift/List/Delete/Consume
            CodesWithSubCode.Add(0xE1); // Guild Role Assign Response
            CodesWithSubCode.Add(0xE2); // Guild Type Change Response
            CodesWithSubCode.Add(0xE5); // Guild Relationship Request Response
            CodesWithSubCode.Add(0xE6); // Guild Relationship Change Response
            CodesWithSubCode.Add(0xEB); // Alliance Kick Response
            CodesWithSubCode.Add(0xF6); // Quest System (Various subcodes)
            CodesWithSubCode.Add(0xF7); // Empire Guardian Enter Response
            CodesWithSubCode.Add(0xF8); // Gens Join/Leave/Reward/Ranking Response
            CodesWithSubCode.Add(0xF9); // Open NPC Dialog
            CodesWithSubCode.Add(0xDE); // Character Class Unlock
            CodesWithSubCode.Add(0x1C); // Map Changed
        }

        public static bool ContainsSubCode(byte code) => CodesWithSubCode.Contains(code);
    }

    /// <summary>
    /// Holds Connect Server packet codes which are known to have sub-codes.
    /// </summary>
    public static class ConnectServerSubCodeHolder
    {
        // Based on ConnectServerPackets.cs from MUnique.OpenMU.Network
        private static readonly HashSet<byte> CodesWithSubCode = new HashSet<byte>
        {
            Hello.Code, // 0x00
            ConnectionInfoRequest.Code, // 0xF4
            ServerListRequest.Code, // 0xF4
            ClientNeedsPatch.Code, // 0x05
            // Add any other codes from ConnectServerPackets.cs which have a SubCode field
        };

        static ConnectServerSubCodeHolder()
        {
            // Explicitly add codes if they are not covered by the above structs
            CodesWithSubCode.Add(0x00); // Hello
            CodesWithSubCode.Add(0xF4); // Server List / Connection Info
            CodesWithSubCode.Add(0x05); // Client Needs Patch
        }

        public static bool ContainsSubCode(byte code) => CodesWithSubCode.Contains(code);
    }
}
