using System;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public partial class BotBrain
    {
        private bool UsesVeteranBuffing => Classic165Mode.IsOn && BotBody?.IsPlayerCompanion == true;
        private bool ManagesRunemasterBladeturn =>
            UsesVeteranBuffing && BotBody.CharacterClass?.ID == (int)eCharacterClass.Runemaster;

        private long _nextRunemasterBladeturnTick;

        public static bool IsGroupBladeturnPulse(Spell spell) =>
            spell is { SpellType: eSpellType.Bladeturn, Target: eSpellTarget.GROUP, IsPulsing: true };

        private bool TryMaintainRunemasterBladeturn()
        {
            if (!ManagesRunemasterBladeturn)
                return false;

            GameBot bot = BotBody;
            int choice = CompanionOptions165.Choice(bot, "runempbt");
            var active = bot.effectListComponent.GetPulseEffects().FirstOrDefault(effect =>
                !effect.IsEnding && !effect.IsEnded && IsGroupBladeturnPulse(effect.SpellHandler?.Spell));
            if (choice == 2)
            {
                active?.End();
                return false;
            }

            long now = GameLoop.GameLoopTime;
            if (active != null || bot.Group is not { MemberCount: > 1 } group || bot.IsCasting ||
                bot.castingComponent.HasPendingSkillRequests || bot.IsCrowdControlled || bot.IsSilenced ||
                bot.IsRecoveryResting || bot.IsOnStableMasterRoute || HasResurrectionDuty() ||
                BladeturnFeedsBoss() || now < _nextRunemasterBladeturnTick)
                return false;

            bool fighting = bot.InCombat || HasAggro || bot.IsAttacking;
            if (!fighting && IsMaintenanceTraveling())
                return false;

            Spell spell = (bot.MiscSpells ?? []).Where(spell => IsGroupBladeturnPulse(spell) && spell.Level <= bot.Level)
                .OrderByDescending(spell => spell.Level).FirstOrDefault();
            if (spell == null || bot.GetSkillDisabledDuration(spell) > 0 ||
                !spell.Uninterruptible && bot.IsBeingInterrupted ||
                !group.GetMembersInTheGroup().Any(member => member != bot && member.IsAlive &&
                    bot.IsWithinRadius(member, Math.Max(350, spell.CalculateEffectiveRange(bot)))))
                return false;

            int reserve = BotSpellPower.PulseCost(bot, spell) * 3;
            if (choice == 0)
                reserve = Math.Max(reserve, bot.MaxMana / 5);
            if (bot.Mana < bot.PowerCost(spell) + reserve)
                return false;

            _nextRunemasterBladeturnTick = now + Math.Max(1750, spell.CastTime + 500);
            var previous = bot.TargetObject;
            try
            {
                bot.TargetObject = bot;
                return bot.CastSpell(spell, m_mobSpellLine, false);
            }
            finally
            {
                bot.TargetObject = previous;
            }
        }

        private int BuffUpkeepPriority(Spell spell) => !Classic165Mode.IsOn ? 0 :
            UsesVeteranBuffing ? VeteranBuffSpellRank(spell.SpellType) : UpkeepPriority165(spell);

        public static int VeteranBuffSpellRank(eSpellType type) => type switch
        {
            eSpellType.EnduranceRegenBuff or eSpellType.PowerRegenBuff => 0,
            eSpellType.StrengthConstitutionBuff or eSpellType.DexterityQuicknessBuff => 1,
            eSpellType.ConstitutionBuff or eSpellType.DexterityBuff => 2,
            eSpellType.AcuityBuff or eSpellType.CombatSpeedBuff => 3,
            eSpellType.StrengthBuff => 4,
            eSpellType.BaseArmorFactorBuff => 6,
            _ => 5,
        };

        public static int VeteranBuffTargetRank(ICharacterClass cls, eSpellType type, int emphasis)
        {
            int role = BuffTargetRank(cls);
            bool spellUser = cls != null && (role == 2 || cls.ClassType == eClassType.ListCaster);
            return emphasis == 2 || emphasis == 0 &&
                type is eSpellType.DexterityBuff or eSpellType.DexterityQuicknessBuff or eSpellType.PowerRegenBuff
                ? spellUser ? 0 : role + 1
                : role;
        }

        private bool PartyBuffHelps(GameLiving target, Spell spell)
        {
            if (!BuffHelps(target, spell))
                return false;

            if (!UsesVeteranBuffing || BotBody.Group?.MemberCount is not > 1)
                return true;

            // In a group, reserve melee strength, haste and endurance for people who use their weapons.
            // This does not remove constitution from the combined strength/constitution buff.
            return spell.SpellType is not (eSpellType.StrengthBuff or eSpellType.CombatSpeedBuff or
                eSpellType.EnduranceRegenBuff) || MeleeBuffHelps(ClassOf(target));
        }
    }
}
