using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using static DOL.GS.ServerRules.IServerRules;

namespace DOL.GS;

/// <summary>
/// Treats a persistent autonomous gamebot as a player only for RvR realm-point
/// credit. Ordinary NPCs, controlled pets, and temporary /spawn companions
/// remain ineligible victims.
/// </summary>
public static class AutonomousBotRealmPointRewards
{
    internal const string LastRealmPointDeathTickProperty = "autonomous.rvr.last.realm.point.death.tick";

    public static bool IsEligibleVictim(GameNPC npc) =>
        npc is GameBot { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false };

    public static int GetPlayerEquivalentRealmPointValue(byte level, int realmLevel)
    {
        // This is the pre-1.81 player formula used by GamePlayer.RealmPointsValue.
        int modifiedLevel = level - 20;
        return Math.Max(1, modifiedLevel * modifiedLevel) + realmLevel;
    }

    public static int CalculateRealmPointReward(int victimRealmPointValue, int victimRealmLevel,
        int awarderRealmPointValue, int awarderRealmLevel, int participantCount,
        int groupContributorCount, double damagePercent, bool applyRealmRankAdjustment)
    {
        if (victimRealmPointValue <= 0 || awarderRealmPointValue <= 0 ||
            participantCount <= 0 || damagePercent <= 0)
        {
            return 0;
        }

        int baseRealmPoints = victimRealmPointValue / participantCount;
        baseRealmPoints = Math.Min(baseRealmPoints, awarderRealmPointValue * 2);
        int realmPoints = (int)(baseRealmPoints * Math.Min(1.0, damagePercent));

        if (applyRealmRankAdjustment)
        {
            realmPoints = (int)(realmPoints *
                (1.0 + 2.0 * (victimRealmLevel - awarderRealmLevel) / 900.0));
        }

        if (groupContributorCount > 1)
            realmPoints += (int)(realmPoints * (groupContributorCount - 1) * 0.125);

        return Math.Max(0, realmPoints);
    }

    /// <summary>What a gamebot is worth in experience to the realm that kills it: a player's value (GamePlayer.ExperienceValue,
    /// four times a monster of its level).</summary>
    public static long GetPlayerEquivalentExperienceValue(int level) =>
        GameServer.ServerRules.GetExperienceForLiving(level) * 4;

    /// <summary>
    /// The experience base for one enemy kill before the damage share, by the player-kill rule
    /// (AbstractServerRules.AwardPlayerOnPlayerKill): the victim's value split among the awarder's contributing
    /// group members, capped at XP_PVP_Cap_Percent of what the awarder's own level is worth.
    /// </summary>
    public static long CappedExperienceBase(long victimExperienceValue, long awarderExperienceValue, int capPercent, int participantCount)
    {
        if (victimExperienceValue <= 0 || awarderExperienceValue <= 0 || participantCount <= 0 || capPercent <= 0)
            return 0;
        return Math.Min(victimExperienceValue / participantCount, awarderExperienceValue * capPercent / 100);
    }

    /// <summary>
    /// Experience for an enemy-realm kill (owner 2026-11-09: battleground kills paid realm points but never experience;
    /// in 1.65 an enemy kill paid both - Prima Shrouded Isles on duels: "There are no Realm Points or Experience points
    /// awarded", the exception). The capped base times the awarder's share of the damage, so one kill never pays out
    /// more than the victim is worth however many fought it.
    /// </summary>
    public static long CalculateExperienceReward(long victimExperienceValue, long awarderExperienceValue, int capPercent,
        int participantCount, double damagePercent)
    {
        if (damagePercent <= 0)
            return 0;
        long baseExperience = CappedExperienceBase(victimExperienceValue, awarderExperienceValue, capPercent, participantCount);
        return Math.Max(0, (long)(baseExperience * Math.Min(1.0, damagePercent)));
    }

    private static readonly DOL.Logging.Logger RvrLog = DOL.Logging.LoggerManager.Create(typeof(AutonomousBotRealmPointRewards));

    /// <summary>
    /// RVR_BOT_KILLED: every gamebot killed by another realm (bot, player, pet or guard), with its siege event
    /// force and where it fell. Siege armies were losing half their strength on the march with no record of it
    /// (run 17, Caer Sursbrooke).
    /// </summary>
    private static void LogRvrDeath(GameBot victim, GameObject killer)
    {
        if (victim?.IsAutonomousWorldBot != true || killer is not GameLiving living || !RvrLog.IsInfoEnabled) return;
        GameLiving owner = ResolveRootRewardOwner(living) ?? living;
        if (owner.Realm == eRealm.None || owner.Realm == victim.Realm) return;
        string force = victim.TempProperties.GetProperty<string>("RvrEventForce") ?? "";
        RvrLog.Info($"RVR_BOT_KILLED victim={victim.Name} id={victim.DatabaseID} realm={GlobalConstants.RealmToName(victim.Realm)} " +
                    $"goal={AutonomousObjectiveAssignments.KindFor(victim)} force={force} killer=\"{owner.Name}\" killerRealm={GlobalConstants.RealmToName(owner.Realm)} " +
                    $"killerType={owner.GetType().Name} region={victim.CurrentRegionID} zone=\"{victim.CurrentZone?.Description}\" " +
                    $"at={victim.X},{victim.Y},{victim.Z}");
    }

    public static void Award(GameBot killedBot, GameObject killer)
    {
        LogRvrDeath(killedBot, killer);
        if (!IsEligibleVictim(killedBot))
            return;

        long now = GameLoop.GameLoopTime;
        long previousDeath = killedBot.TempProperties.GetProperty<long>(
            LastRealmPointDeathTickProperty, -1);
        long worthInterval = Math.Max(0, Properties.RP_WORTH_SECONDS) * 1000L;
        bool isWorthRealmPoints = previousDeath < 0 || now - previousDeath >= worthInterval;

        // Every death resets the same repeat-kill window used for a real player,
        // including a death caused by an NPC or by somebody who receives no RP.
        killedBot.TempProperties.SetProperty(LastRealmPointDeathTickProperty, now);

        KeyValuePair<GameLiving, double>[] rawContributors;
        lock (killedBot.XpGainersLock)
            rawContributors = killedBot.XPGainers.ToArray();

        Dictionary<GameLiving, double> hostileContributors = new();
        foreach (KeyValuePair<GameLiving, double> pair in rawContributors)
        {
            GameLiving credited = ResolveRootRewardOwner(pair.Key);
            if (credited == null || credited.Realm == eRealm.None || credited.Realm == killedBot.Realm)
                continue;

            hostileContributors[credited] = hostileContributors.TryGetValue(credited, out double existing)
                ? existing + pair.Value
                : pair.Value;
        }

        double totalDamage = hostileContributors.Sum(pair => pair.Value);
        if (totalDamage <= 0)
            return;

        int victimRealmPointValue = GetPlayerEquivalentRealmPointValue(killedBot.Level, killedBot.RealmLevel);
        long victimExperienceValue = GetPlayerEquivalentExperienceValue(killedBot.Level);
        var sharedRealmPoints = PayHiredGroupRealmPoints(killedBot, victimRealmPointValue, killedBot.RealmLevel,
            hostileContributors, totalDamage, isWorthRealmPoints);
        if (isWorthRealmPoints)
            PayGamebots(killedBot, victimRealmPointValue, killedBot.RealmLevel, hostileContributors, totalDamage,
                victimExperienceValue, sharedRealmPoints);

        Dictionary<GamePlayer, EntityCountTotalDamagePair> playerContributions = new();
        Dictionary<Group, EntityCountTotalDamagePair> groupContributions = new();

        foreach (KeyValuePair<GameLiving, double> pair in hostileContributors)
        {
            // Persistent gamebots remain part of the damage denominator, just
            // like another real participant, but this path only pays connected
            // players. Temporary companions have already resolved to the owner.
            if (pair.Key is not GamePlayer player ||
                player.ObjectState is not GameObject.eObjectState.Active ||
                !player.IsWithinRadius(killedBot, WorldMgr.MAX_EXPFORKILL_DISTANCE))
            {
                continue;
            }

            AddContribution(player, pair.Value, player, playerContributions);
            if (player.Group != null)
                AddContribution(player, pair.Value, player.Group, groupContributions);
        }

        RecordHiredGroupSupportKills(killedBot, sharedRealmPoints, playerContributions);
        GameLiving creditedKiller = ResolveRootRewardOwner(killer as GameLiving);

        foreach (KeyValuePair<GamePlayer, EntityCountTotalDamagePair> pair in playerContributions)
        {
            GamePlayer player = pair.Key;
            lock (player.AwardLock)
            {
                EntityCountTotalDamagePair contribution = pair.Value;
                if (player.Group != null && groupContributions.TryGetValue(player.Group, out EntityCountTotalDamagePair group))
                    contribution = group;

                double damagePercent = Math.Min(1.0, contribution.Damage / totalDamage);
                int contributorCount = Math.Max(1, contribution.Count);
                int groupContributorCount = player.Group == null ? 1 : contributorCount;
                bool shared = sharedRealmPoints != null && sharedRealmPoints.ContainsKey(player);
                int realmPointsEarned = shared ? sharedRealmPoints[player] : 0;

                if (isWorthRealmPoints)
                {
                    if (!shared)
                    {
                        realmPointsEarned = CalculateRealmPointReward(victimRealmPointValue, killedBot.RealmLevel,
                            player.RealmPointsValue, player.RealmLevel, contributorCount,
                            groupContributorCount, damagePercent, ApplyRankAdjustment(player, player.RealmLevel));

                        if (realmPointsEarned > 0)
                            player.GainRealmPoints(realmPointsEarned, true);
                    }

                    // experience too, by the player-kill rule (with its outpost bonus)
                    long experienceBase = CappedExperienceBase(victimExperienceValue, player.ExperienceValue,
                        Properties.XP_PVP_CAP_PERCENT, contributorCount);
                    long experience = (long)(experienceBase * damagePercent);
                    if (experience > 0)
                        experience += ServerRules.AbstractServerRules.CalculateOutpostExperienceBonus(player, experienceBase);
                    if (experience > 0)
                        player.GainExperience(eXPSource.Player, experience);
                }
                else
                {
                    string victimName = AutonomousNameMask.NameFor(player, killedBot, 0, true);
                    player.Out.SendMessage($"{victimName} has been killed recently and is worth no realm points!",
                        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"{victimName} has been killed recently and is worth no experience!",
                        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                bool deathBlow = ReferenceEquals(player, creditedKiller);
                bool soloKill = !shared && damagePercent >= 1.0 && contributorCount == 1;
                player.UpdateKillStatsOnPlayerKill(killedBot.Realm, deathBlow, soloKill, realmPointsEarned);
            }
        }
    }

    /// <summary>
    /// Gamebots earn realm points for PvP kills like players: a share of the victim's value by
    /// their part of the damage, with the same group bonus. Only persistent gamebots of a hostile
    /// realm within reward range (pets resolve to their bot). They earn experience for the kill by the same shares
    /// (victimExperienceValue; 0 pays none), by the player-kill rule.
    /// </summary>
    public static void PayGamebots(GameLiving victim, int victimRealmPointValue, int victimRealmLevel,
        IReadOnlyDictionary<GameLiving, double> hostileContributors, double totalDamage, long victimExperienceValue = 0,
        IReadOnlyDictionary<GameLiving, int> sharedRealmPoints = null)
    {
        if (victim == null || victimRealmPointValue <= 0 || totalDamage <= 0) return;
        // True 1.65 Mode (owner 2026-10-30): a hired companion earns realm points from its group's kills like a group
        // member, even when it landed no blow itself: it shares in its group's damage.
        var withCompanions = new Dictionary<GameLiving, double>(hostileContributors);
        foreach (Group group in hostileContributors.Keys.Select(k => k.Group).Where(g => g != null).Distinct().ToList())
            foreach (GameBot companion in group.GetMembersInTheGroup().OfType<GameBot>().Where(HiredCompanions165.IsHired))
                withCompanions.TryAdd(companion, 0);
        hostileContributors = withCompanions;
        var bots = hostileContributors
            .Where(pair => pair.Key is GameBot { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false } bot &&
                bot.Realm != eRealm.None && bot.Realm != victim.Realm && bot.ObjectState == GameObject.eObjectState.Active &&
                bot.IsWithinRadius(victim, WorldMgr.MAX_EXPFORKILL_DISTANCE))
            .Select(pair => (Bot: (GameBot)pair.Key, Damage: pair.Value)).ToArray();
        foreach (var (bot, damage) in bots)
        {
            int groupCount = bot.Group == null ? 1 : Math.Max(1, bots.Count(other => other.Bot.Group == bot.Group));
            double groupDamage = bot.Group == null ? damage : bots.Where(other => other.Bot.Group == bot.Group).Sum(other => other.Damage);
            int points = CalculateRealmPointReward(victimRealmPointValue, victimRealmLevel,
                GetPlayerEquivalentRealmPointValue(bot.Level, bot.RealmLevel), bot.RealmLevel,
                groupCount, groupCount, Math.Min(1.0, groupDamage / totalDamage), true);
            if (points > 0 && sharedRealmPoints?.ContainsKey(bot) != true)
                bot.GainRealmPoints(points, true);
            long experience = CalculateExperienceReward(victimExperienceValue, GetPlayerEquivalentExperienceValue(bot.Level),
                Properties.XP_PVP_CAP_PERCENT, groupCount, groupDamage / totalDamage);
            if (experience > 0) bot.GainExperience(eXPSource.Player, experience);
        }
    }

    /// <summary>Pays mixed-group RP and the existing gamebot rewards for a player victim.</summary>
    public static IReadOnlyDictionary<GameLiving, int> PayGamebotsForPlayerKill(GamePlayer killedPlayer)
    {
        KeyValuePair<GameLiving, double>[] raw;
        lock (killedPlayer.XpGainersLock)
            raw = killedPlayer.XPGainers.ToArray();
        var hostile = new Dictionary<GameLiving, double>();
        foreach (var pair in raw)
        {
            GameLiving credited = ResolveRootRewardOwner(pair.Key);
            if (credited == null || credited.Realm == eRealm.None || credited.Realm == killedPlayer.Realm) continue;
            hostile[credited] = hostile.GetValueOrDefault(credited) + pair.Value;
        }
        // experience under the player rule's own repeat-kill window (AwardPlayerOnPlayerKill.isWorthAnything)
        bool worthExperience = killedPlayer.DeathTime + Properties.RP_WORTH_SECONDS <= killedPlayer.PlayedTime;
        var sharedRealmPoints = PayHiredGroupRealmPoints(killedPlayer, killedPlayer.RealmPointsValue, killedPlayer.RealmLevel,
            hostile, raw.Sum(pair => pair.Value), worthExperience);
        PayGamebots(killedPlayer, killedPlayer.RealmPointsValue, killedPlayer.RealmLevel, hostile, hostile.Sum(pair => pair.Value),
            worthExperience ? killedPlayer.ExperienceValue : 0, sharedRealmPoints);
        return sharedRealmPoints;
    }

    public static IEnumerable<(GameLiving[] Members, double DamageShare)> HiredGroupShares(
        GameLiving victim, IReadOnlyDictionary<GameLiving, double> contributors, double totalDamage, Func<GameBot, bool> isHired)
    {
        if (totalDamage <= 0)
            yield break;

        var groups = new HashSet<Group>();
        foreach (var (contributor, damage) in contributors)
        {
            if (damage <= 0 || contributor.Group is not { } group || !groups.Add(group))
                continue;

            List<GameLiving> roster = group.GetMembersInTheGroup();
            GameBot[] hired = roster.OfType<GameBot>().Where(bot =>
                bot is { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false } && isHired(bot)).ToArray();
            if (hired.Length == 0 || !roster.Any(member => member is GamePlayer))
                continue;

            GameLiving[] eligible = roster.Where(member => member.Group == group &&
                (member is GamePlayer || member is GameBot bot && hired.Contains(bot)) &&
                member.Realm != eRealm.None && member.Realm != victim.Realm &&
                member.ObjectState == GameObject.eObjectState.Active &&
                member.IsWithinRadius(victim, WorldMgr.MAX_EXPFORKILL_DISTANCE)).ToArray();
            double groupDamage = eligible.Sum(member => contributors.GetValueOrDefault(member));
            if (groupDamage > 0)
                yield return (eligible, Math.Min(1.0, groupDamage / totalDamage));
        }
    }

    private static Dictionary<GameLiving, int> PayHiredGroupRealmPoints(GameLiving victim, int victimValue,
        int victimRealmLevel, IReadOnlyDictionary<GameLiving, double> contributors, double totalDamage, bool worthRealmPoints)
    {
        Dictionary<GameLiving, int> awarded = null;
        foreach (var (members, share) in HiredGroupShares(victim, contributors, totalDamage, HiredCompanions165.IsHired))
        {
            foreach (GameLiving member in members)
            {
                int points = 0;
                if (member is GamePlayer player)
                {
                    lock (player.AwardLock)
                    {
                        if (worthRealmPoints)
                        {
                            points = CalculateRealmPointReward(victimValue, victimRealmLevel, player.RealmPointsValue,
                                player.RealmLevel, members.Length, members.Length, share, ApplyRankAdjustment(player, player.RealmLevel));
                            if (points > 0)
                                player.GainRealmPoints(points, true);
                        }
                    }
                }
                else if (member is GameBot bot && worthRealmPoints)
                {
                    points = CalculateRealmPointReward(victimValue, victimRealmLevel,
                        GetPlayerEquivalentRealmPointValue(bot.Level, bot.RealmLevel), bot.RealmLevel,
                        members.Length, members.Length, share, ApplyRankAdjustment(bot, bot.RealmLevel));
                    if (points > 0)
                        bot.GainRealmPoints(points, true);
                }
                (awarded ??= new())[member] = points;
            }
        }
        return awarded;
    }

    private static bool ApplyRankAdjustment(GameLiving member, int realmLevel)
    {
        DbBattleground battleground = GameServer.KeepManager.GetBattleground(member.CurrentRegionID);
        return battleground == null || realmLevel < battleground.MaxRealmLevel;
    }

    internal static void RecordHiredGroupSupportKills(GameLiving victim, IReadOnlyDictionary<GameLiving, int> sharedRealmPoints,
        IReadOnlyDictionary<GamePlayer, EntityCountTotalDamagePair> playerContributions)
    {
        if (sharedRealmPoints == null)
            return;

        foreach (var (member, points) in sharedRealmPoints)
        {
            if (member is GamePlayer player && !playerContributions.ContainsKey(player))
                player.UpdateKillStatsOnPlayerKill(victim.Realm, false, false, points);
        }
    }

    /// <summary>Realm level for a realm point total, from the player table (RR1L0 = 0 ... RR5L3 = 43).</summary>
    public static int RealmLevelFor(long realmPoints)
    {
        if (realmPoints <= 0) return 0;
        long[] table = GamePlayer.REALMPOINTS_FOR_LEVEL;
        for (int level = table.Length - 1; level > 0; level--)
            if (table[level] <= realmPoints) return level;
        return 0;
    }

    public static GameLiving ResolveRootRewardOwner(GameLiving source)
    {
        GameLiving current = source;
        for (int depth = 0; depth < 16 && current is GameNPC npc &&
             npc.Brain is IControlledBrain controlled &&
             controlled.GetLivingOwner() is GameLiving owner; depth++)
        {
            current = owner;
        }

        return current;
    }

    private static void AddContribution<T>(GameLiving participant, double damage, T entity,
        Dictionary<T, EntityCountTotalDamagePair> contributions) where T : class, IGameStaticItemOwner
    {
        if (contributions.TryGetValue(entity, out EntityCountTotalDamagePair value))
        {
            value.Count++;
            value.Damage += damage;
            if (value.HighestLevelPlayer.Level < participant.Level)
                value.HighestLevelPlayer = participant;
        }
        else
        {
            contributions[entity] = new EntityCountTotalDamagePair(1, damage, participant);
        }
    }
}
