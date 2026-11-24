using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using DOL.GS.Keeps;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// A realm's battleground assault goes in as one wave. Run 5 (2026-10-14): in all four battlegrounds about half of the
/// bots fought the other realms in the open field (median 3,700-5,600 units from the central keep) and at most a
/// handful of any realm stood at the keep, so the gates were broken and rebuilt for hours and no lord was ever
/// touched. Now the realm's bots gather at a staging point on their side of the keep and assault together once most
/// of them are there (or after four minutes with at least eight); when the wave is spent they gather again.
/// </summary>
public static class AutonomousBattlegroundWave
{
    private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousBattlegroundWave));

    public const float StagingDistance = 2_500f;
    public const int MinimumWave = 8;
    public const long GatherPatienceMilliseconds = 4 * 60_000;
    public const long WaveSpentCheckMilliseconds = 3 * 60_000;
    public const int WaveSpentNearKeep = 4;

    private sealed class Wave
    {
        public Vector3? Staging;
        public long GatherSince, GoingSince, NextCount, NextTotalCount;
        public bool Going;
        public eRealm KeepHolder;
        public int Total, Gathered, NearKeep;
    }

    private static readonly ConcurrentDictionary<(ushort Region, eRealm Realm), Wave> Waves = new();

    /// <summary>
    /// Too few of the realm's bots in the battleground for any wave (fewer than <see cref="MinimumWave"/>): they roam the
    /// field instead of the keep. The owner's 2026-10-17 session: Thidranki held 1-2 bots per realm (the roster has 47
    /// bots of levels 20-24); they waited at the staging point for a wave of eight that never came, and single bots
    /// that strayed into the keep's guards announced sieges that went nowhere.
    /// </summary>
    public static bool TooFewForAssault(eRealm realm, BattlegroundBrackets.Bracket bracket)
    {
        if (bracket == null) return false;
        long now = GameLoop.GameLoopTime;
        Wave wave = Waves.GetOrAdd((bracket.RegionId, realm), _ => new Wave { GatherSince = now });
        lock (wave)
        {
            if (now >= wave.NextTotalCount)
            {
                wave.NextTotalCount = now + 5_000;
                // PvP-focus bots only (battlegrounds fix 2026-11-12): bots of the bracket also grind the battleground's
                // monsters now, and they are no siege army.
                wave.Total = AutonomousBotRegistry.Snapshot().Count(other => other.IsAlive && other.Realm == realm &&
                    other.CurrentRegionID == bracket.RegionId && AutonomousObjectiveAssignments.IsBattlegroundPvp(other));
            }
            return IsTooFew(wave.Total);
        }
    }

    public static bool IsTooFew(int realmBotsInside) => realmBotsInside < MinimumWave;

    /// <summary>The launch quorum is only for starting a wave. Let survivors who are still working
    /// the keep finish their current assault, and give the rest time to reach it after a casualty.</summary>
    public static bool AllowsAssault(int inside, bool launched, int nearKeep, long elapsed)
    {
        return !IsTooFew(inside) ||
               launched && inside >= WaveSpentNearKeep &&
               (elapsed < WaveSpentCheckMilliseconds || nearKeep >= WaveSpentNearKeep);
    }

    public static bool CanAssault(GameBot bot, BattlegroundBrackets.Bracket bracket, AbstractGameKeep central)
    {
        long now = GameLoop.GameLoopTime;
        Wave wave = Waves.GetOrAdd((bracket.RegionId, bot.Realm), _ => new Wave { GatherSince = now });
        lock (wave)
        {
            if (wave.KeepHolder != central.Realm)
            {
                wave.KeepHolder = central.Realm;
                wave.GatherSince = now;
                wave.Going = false;
                wave.NextCount = 0;
            }

            RefreshAttendance(wave, bot.Realm, bracket, central, now);
            bool allowed = AllowsAssault(wave.Total, wave.Going, wave.NearKeep, now - wave.GoingSince);
            if (!allowed && wave.Going)
                EndWave(wave, bot.Realm, bracket, now);
            return allowed;
        }
    }

    /// <summary>A wave goes in at this many gathered: 60% of the realm's bots inside, at least <see cref="MinimumWave"/>.</summary>
    public static int WaveSize(int realmBotsInside) => Math.Max(MinimumWave, realmBotsInside * 6 / 10);

    /// <summary>
    /// True while this bot should gather at <paramref name="staging"/> instead of assaulting; false when its realm's
    /// wave is going in (or no staging point could be found), so the ordinary assault runs.
    /// </summary>
    public static bool ShouldGather(GameBot bot, BattlegroundBrackets.Bracket bracket, AbstractGameKeep central, out Vector3 staging,
        out string progress)
    {
        staging = default; progress = string.Empty;
        // Gates down: everyone goes for the lord. In the owner's 2026-10-14 session both gates of Dun Abermenai stood
        // broken for over an hour at a time while the realms kept gathering for waves, and the lord was never touched.
        if (!central.Doors.Values.Any(door => door.IsAlive && door.IsAttackableDoor && door.State == eDoorState.Closed))
            return false;
        long now = GameLoop.GameLoopTime;
        Wave wave = Waves.GetOrAdd((bracket.RegionId, bot.Realm), _ => new Wave { GatherSince = now });
        lock (wave)
        {
            if (!wave.Staging.HasValue)
            {
                wave.Staging = StagingPoint(bot, bracket, central);
                if (wave.Staging.HasValue)
                    wave.NextCount = 0; // Include the newly selected staging point in the attendance sample.
            }
            if (!wave.Staging.HasValue) return false;
            staging = wave.Staging.Value;
            RefreshAttendance(wave, bot.Realm, bracket, central, now);
            if (wave.Going)
            {
                if (now - wave.GoingSince >= WaveSpentCheckMilliseconds && wave.NearKeep < WaveSpentNearKeep)
                {
                    EndWave(wave, bot.Realm, bracket, now);
                }
                else return false;
            }
            int needed = WaveSize(wave.Total);
            bool patient = now - wave.GatherSince >= GatherPatienceMilliseconds && wave.Gathered >= MinimumWave;
            if (wave.Gathered >= needed || patient)
            {
                wave.Going = true;
                wave.GoingSince = now;
                Log.Info($"BG_WAVE_ASSAULT battleground={bracket.Name} realm={GlobalConstants.RealmToName(bot.Realm)} keep=\"{central.Name}\" " +
                         $"gathered={wave.Gathered} needed={needed} inside={wave.Total} minutes={(now - wave.GatherSince) / 60_000.0:0.0}");
                return false;
            }
            progress = $"{wave.Gathered}/{needed} gathered";
            return true;
        }
    }

    private static void RefreshAttendance(Wave wave, eRealm realm, BattlegroundBrackets.Bracket bracket,
        AbstractGameKeep central, long now)
    {
        if (now < wave.NextCount) return;
        wave.NextCount = wave.NextTotalCount = now + 5_000;
        var keepPoint = new Point3D(central.X, central.Y, central.Z);
        var stage = wave.Staging;
        var stagePoint = stage.HasValue ? new Point3D((int)stage.Value.X, (int)stage.Value.Y, (int)stage.Value.Z) : null;

        wave.Total = wave.Gathered = wave.NearKeep = 0;
        foreach (GameBot other in AutonomousBotRegistry.Snapshot())
        {
            if (!other.IsAlive || other.Realm != realm || other.CurrentRegionID != bracket.RegionId ||
                !AutonomousObjectiveAssignments.IsBattlegroundPvp(other))
                continue;

            wave.Total++;
            if (stagePoint != null && other.IsWithinRadius(stagePoint, 900)) wave.Gathered++;
            if (other.IsWithinRadius(keepPoint, 2_500)) wave.NearKeep++;
        }
    }

    private static void EndWave(Wave wave, eRealm realm, BattlegroundBrackets.Bracket bracket, long now)
    {
        wave.Going = false;
        wave.GatherSince = now;
        Log.Info($"BG_WAVE_SPENT battleground={bracket.Name} realm={GlobalConstants.RealmToName(realm)} " +
                 $"nearKeep={wave.NearKeep} inside={wave.Total}");
    }

    /// <summary>On the realm's side of the keep: from the keep toward the realm's field arrival, on the navmesh.</summary>
    private static Vector3? StagingPoint(GameBot bot, BattlegroundBrackets.Bracket bracket, AbstractGameKeep central)
    {
        GameLocation arrival = BattlegroundBrackets.BotArrival(bracket, bot.Realm);
        if (arrival == null) return null;
        Vector3 keep = new(central.X, central.Y, central.Z);
        Vector3 toward = new Vector3(arrival.X, arrival.Y, arrival.Z) - keep;
        float length = toward.Length();
        if (length < 1) return null;
        Vector3 raw = keep + toward / length * Math.Min(StagingDistance, length * 0.8f);
        Zone zone = WorldMgr.GetRegion(bracket.RegionId)?.GetZone((int)raw.X, (int)raw.Y);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return null;
        return nav.GetClosestPoint(zone, raw, 600, 600, 2_000, nav.DefaultFilters);
    }
}
