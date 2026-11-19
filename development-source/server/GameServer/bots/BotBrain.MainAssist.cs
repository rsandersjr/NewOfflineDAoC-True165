using System.Runtime.CompilerServices;
using DOL.GS;

namespace DOL.AI.Brain
{
    /// <summary>
    /// True 1.65 Mode: a gamebot party has a main assist, as period groups did. The tank fighting first sets the
    /// party's kill target (its current target); the other members prefer it over their own threat, unless they are
    /// below half health and being hit, then they fight back. Before 2026-11-06 every bot fought its own highest threat,
    /// so damage was spread over several monsters. Player-led companions already assist their player and are unchanged.
    /// Lock-free: one small holder per group, read and written by the members' own thinks. PvE parties only: an RvR
    /// warband keeps its members' own targets.
    /// </summary>
    public partial class BotBrain
    {
        public const int MainAssistLowHealthPercent = 50;
        public const long KillTargetStaleMilliseconds = 10_000;

        private sealed class KillTargetHolder
        {
            public volatile GameLiving Writer;
            public volatile GameLiving Target;
            public long Tick;
        }

        private static readonly ConditionalWeakTable<Group, KillTargetHolder> KillTargets = new();

        private bool UsesGamebotMainAssist =>
            Classic165Mode.IsOn && BotBody is { IsAutonomousWorldBot: true, IsPlayerLedGroup: false, IsTemporaryGroupHelper: false } &&
            Body.Group != null && AutonomousObjectiveAssignments.KindFor(BotBody) == eAutonomousObjectiveKind.GroupPve;

        /// <summary>
        /// M7 / G5 (veteran review 2026-11-11; period: "nominate a main assist ... CC works first, then all hit the MA's
        /// target"): in a player's group, while the player is not attacking (a healer, a mezzer, someone busy), the tank
        /// companion is the main assist - its target is the group's kill target and the other companions take it. While
        /// the player attacks, the companions assist the player exactly as before.
        /// </summary>
        public static bool CompanionMainAssistApplies(bool classic165, bool companion, bool playerLed, bool grouped,
            bool playerAttacking) =>
            classic165 && companion && playerLed && grouped && !playerAttacking;

        /// <summary>The player is fighting: swinging, or casting a harmful spell that is not crowd control.</summary>
        private static bool PlayerLeadsFight(GamePlayer player)
        {
            if (player?.IsAlive != true)
                return false;
            Spell casting = player.castingComponent?.SpellHandler?.Spell;
            return player.IsAttacking || player.IsCasting && casting?.IsHarmful == true && !PlayerLedPullCoordinator.IsCrowdControl(casting);
        }

        /// <summary>
        /// How long the player still counts as leading the fight after their last swing or harmful cast (combat review
        /// 2026-11-11): a caster's or archer's gap between casts, or a power wait, is not "the player stopped".
        /// </summary>
        public const long PlayerLeadsStickyMilliseconds = 4_000;

        /// <summary>Pure: the player led the fight at <paramref name="lastLedTick"/> and still counts as leading now.</summary>
        public static bool PlayerStillLeads(long now, long lastLedTick) => now - lastLedTick < PlayerLeadsStickyMilliseconds;

        private long _playerLedFightTick = long.MinValue / 2;
        private GamePlayer _playerLedFightPlayer;

        /// <summary><see cref="PlayerLeadsFight"/>, held for <see cref="PlayerLeadsStickyMilliseconds"/> after it was last seen.</summary>
        private bool PlayerLeadsFightSticky(GamePlayer player)
        {
            long now = GameLoop.GameLoopTime;
            if (PlayerLeadsFight(player))
            {
                _playerLedFightPlayer = player;
                _playerLedFightTick = now;
                return true;
            }
            return player?.IsAlive == true && player == _playerLedFightPlayer && PlayerStillLeads(now, _playerLedFightTick);
        }

        private bool UsesCompanionMainAssist =>
            CompanionMainAssistApplies(Classic165Mode.IsOn, BotBody?.IsPlayerCompanion == true, BotBody?.IsPlayerLedGroup == true,
                Body.Group != null, PlayerLeadsFightSticky(AssistedPlayer));

        private bool UsesMainAssist => UsesGamebotMainAssist || UsesCompanionMainAssist;

        /// <summary>The tank companion's kill target while its player is not attacking (M7), or null.</summary>
        private GameLiving CompanionKillTarget() =>
            UsesCompanionMainAssist && Body.Group is { } group && KillTargets.TryGetValue(group, out KillTargetHolder holder) &&
            GameLoop.GameLoopTime - holder.Tick <= KillTargetStaleMilliseconds && holder.Target is { IsAlive: true } target
                ? target : null;

        /// <summary>With the pull complete, idle melee companions can join their tank before the victim's first swing.</summary>
        private void JoinStunnedCompanionKillTarget()
        {
            GamePlayer leader = AssistedPlayer;
            if (!Classic165Mode.IsOn || BotBody?.IsPlayerCompanion != true || HasAggro || Body.IsAttacking ||
                Body.IsCrowdControlled || Body.IsCasting || !PlayerLedPullCoordinator.Available(BotBody, leader) ||
                PrefersCurrentSpellRange() || SupportSitsOutFights() ||
                CompanionKillTarget() is not GameNPC { IsStunned: true } target || target is GameBot ||
                BotPvpCrowdControl.PlayerLike(target) || IsBreakablyControlled(target) || IsReservedForMez(target) ||
                PlayerLedPullCoordinator.LeaderControlTarget(leader) == target ||
                !Body.IsWithinRadius(target, GROUP_DEFENSE_ASSIST_RADIUS) || !CanDefendAgainst(target))
                return;

            if (BotBody.IsRecoveryResting)
                BotBody.WakeRecoveryRest();
            AddToAggroList(target, 1);
            FSM.SetCurrentState(eFSMStateType.AGGRO);
        }

        /// <summary>Pure rule: a member follows the kill target unless it is mesmerized/rooted, or the member is low and being hit.</summary>
        public static bool AssistsKillTarget(bool killTargetValid, bool killTargetControlled, int healthPercent, bool beingHit) =>
            killTargetValid && !killTargetControlled && !(healthPercent < MainAssistLowHealthPercent && beingHit);

        /// <summary>
        /// Pure rule: the stored kill target is replaced only when it is gone, when its own writer offers a new one, when
        /// that writer has left it, or when nobody has confirmed it for a while. A second tank on an add does not take over.
        /// </summary>
        public static bool ReplacesKillTarget(bool storedAlive, bool sameWriter, bool writerStillOnIt, long ageMilliseconds) =>
            !storedAlive || sameWriter || !writerStillOnIt || ageMilliseconds > KillTargetStaleMilliseconds;

        /// <summary>
        /// Pure (M2, veteran review 2026-11-11): whether this tank may write the party's kill target. With a main tank
        /// alive and near, only the main tank writes; with none, any tank (as before). A peel (a tank going for an add on a
        /// groupmate) may start a kill target when there is none, but never moves a live one.
        /// </summary>
        public static bool MayWriteKillTarget(bool mainTankNear, bool isMainTank, bool peel, bool liveStoredTarget) =>
            (!mainTankNear || isMainTank) && !(peel && liveStoredTarget);

        /// <summary>The tank publishes the monster it is fighting as the party's kill target.</summary>
        private void OfferKillTarget(GameLiving target, bool peel = false)
        {
            if (!IsTankClass || !UsesMainAssist || target is not GameNPC { IsAlive: true } ||
                !(Body.IsAttacking || Body.InCombat) || IsBreakablyControlled(target))
                return;
            // a companion tank's peel on an add never becomes the kill target of the player's group (combat review
            // 2026-11-11): the others stay on the tank's main monster instead of flipping onto the add
            if (peel && BotBody?.IsPlayerLedGroup == true)
                return;
            KillTargetHolder holder = KillTargets.GetValue(Body.Group, _ => new KillTargetHolder());
            long now = GameLoop.GameLoopTime;
            GameLiving stored = holder.Target, writer = holder.Writer;
            GameLiving mainTank = BotGroupRoles.MainTank(Body);
            bool liveStored = stored != null && stored != target && stored.IsAlive &&
                stored.ObjectState == GameObject.eObjectState.Active && now - holder.Tick <= KillTargetStaleMilliseconds;
            if (!MayWriteKillTarget(mainTank != null && mainTank != Body && (mainTank.InCombat || mainTank.IsAttacking) &&
                    Body.IsWithinRadius(mainTank, GROUP_DEFENSE_ASSIST_RADIUS),
                    mainTank == Body, peel, liveStored))
                return;
            if (stored == target && writer == Body)
            {
                holder.Tick = now;
                return;
            }
            if (stored != null && !ReplacesKillTarget(stored.IsAlive && stored.ObjectState == GameObject.eObjectState.Active,
                    writer == Body, writer is { IsAlive: true } && writer.TargetObject == stored, now - holder.Tick))
                return;
            holder.Writer = Body;
            holder.Target = target;
            holder.Tick = now;
        }

        /// <summary>
        /// The party's kill target for a non-tank member, or null to keep its own choice. M2 (veteran review 2026-11-11):
        /// an off-tank with no peel to make and no add on itself hits the main tank's kill too (Prima: take the adds off
        /// the healers, then return to the main mob); the main tank keeps its own target.
        /// </summary>
        private GameLiving MainAssistTarget(GameLiving directAttacker)
        {
            if (IsTankClass && !(Classic165Mode.IsOn && directAttacker == null && BotGroupRoles.IsOffTank(Body)) ||
                Body.Group is not { } group || !KillTargets.TryGetValue(group, out KillTargetHolder holder) ||
                !UsesMainAssist)
                return null;
            GameLiving target = holder.Target;
            bool valid = target != null && GameLoop.GameLoopTime - holder.Tick <= KillTargetStaleMilliseconds &&
                CanDefendAgainst(target) && Body.IsWithinRadius(target, GROUP_DEFENSE_ASSIST_RADIUS);
            if (!AssistsKillTarget(valid, valid && IsBreakablyControlled(target), Body.HealthPercent, directAttacker != null))
                return null;
            // /defensive and /passive: never a target the companion's stance would drop again on its next think
            if (BotBody?.IsPlayerCompanion == true && !CompanionEngagementMode.Allows(Body, target))
                return null;
            if (!AggroList.ContainsKey(target))
                AddToAggroList(target, 1);
            return target;
        }
    }
}
