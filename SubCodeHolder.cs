using MUnique.OpenMU.Network.Packets.ServerToClient;

namespace MuOnlineConsole
{
    /// <summary>
    /// Helper class that defines which packet codes use subcodes in their headers.
    /// </summary>
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
            CharacterLevelUpdate.Code,
            CharacterStatIncreaseResponse.Code,
            MasterStatsUpdate.Code,
            MasterCharacterLevelUpdate.Code,
            MasterSkillLevelUpdate.Code,
            MasterSkillList.Code
        };

        public static bool ContainsSubCode(byte code) => CodesWithSubCode.Contains(code);
    }
}