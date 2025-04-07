using MUnique.OpenMU.Network.Packets.ServerToClient;

namespace MuOnlineConsole
{
    public static class SubCodeHolder
    {
        private static readonly HashSet<byte> CodesWithSubCode = new HashSet<byte>
        {
            LoginResponse.Code,
            CharacterList.Code,
            NpcItemBuyFailed.Code,
            TradeMoneySetResponse.Code,
            PlayerShopSetItemPriceResponse.Code,
            CurrentHealthAndShield.Code,
            CurrentManaAndAbility.Code,
            LegacyQuestMonsterKillInfo.Code,
            DuelStartResult.Code,
            ChaosCastleEnterResult.Code,
            IllusionTempleEnterResult.Code,
            QuestEventResponse.Code,
            OpenNpcDialog.Code,
            CharacterClassCreationUnlock.Code,
            MasterCharacterLevelUpdate.Code,
            MasterSkillLevelUpdate.Code,
            MasterSkillList.Code,
            MapChanged.Code
        };

        static SubCodeHolder()
        {
            CodesWithSubCode.Add(0xF1); CodesWithSubCode.Add(0xF3); CodesWithSubCode.Add(0x32);
            CodesWithSubCode.Add(0x37); CodesWithSubCode.Add(0x3F); CodesWithSubCode.Add(0x26);
            CodesWithSubCode.Add(0x27); CodesWithSubCode.Add(0xA3); CodesWithSubCode.Add(0xAA);
            CodesWithSubCode.Add(0xAF); CodesWithSubCode.Add(0xB2); CodesWithSubCode.Add(0xF6);
            CodesWithSubCode.Add(0x30); CodesWithSubCode.Add(0x1C);
        }

        public static bool ContainsSubCode(byte code) => CodesWithSubCode.Contains(code);
    }
}