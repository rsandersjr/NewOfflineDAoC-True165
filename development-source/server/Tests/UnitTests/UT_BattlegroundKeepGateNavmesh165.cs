using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS.Keeps;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>The door box a door registers its navmesh polygons with (LocalPathfindingMgr.RegisterDoor).</summary>
[TestFixture]
public class UT_BattlegroundKeepDoorRegistration
{
    [TestCase(false, true, (ushort)250, 120f)]   // battleground keep doors: the Hfrontkeep doorway slivers reach 113
    [TestCase(false, true, (ushort)253, 120f)]
    [TestCase(false, true, (ushort)1, 64f)]      // frontier keep doors keep the usual box
    [TestCase(false, true, (ushort)249, 64f)]
    [TestCase(false, false, (ushort)251, 64f)]   // a plain door in a battleground region
    [TestCase(true, false, (ushort)1, GameRealmGate.DoorwayHalfWidth)]   // realm gates: the whole doorway
    public void DoorSearchHalfWidth(bool realmGate, bool keepDoor, ushort region, float expected) =>
        Assert.That(LocalPathfindingMgr.DoorSearchHalfWidth(realmGate, keepDoor, region), Is.EqualTo(expected));

    [Test]
    public void BattlegroundBoxStaysClearOfTheOtherGate()
    {
        // 2026-11-12 offline check of navmesh-165 zone250-253 (live and fixed): the farthest doorway sliver needs 113, and
        // the first polygon in two doors' boxes appears at 224 (Thidranki's two gates stand 289 apart).
        Assert.That(LocalPathfindingMgr.BattlegroundKeepDoorHalfWidth, Is.GreaterThanOrEqualTo(113f).And.LessThan(224f));
    }
}

/// <summary>
/// True 1.65 keep gates on the installed (or staged) navmesh-165 maps, read-only (lane BGNAV 2026-11-12, BG250 N1): every
/// keep with a 01/02 gate pair in the battlegrounds (250-253: the central keep and the three portal keeps) and in the
/// frontiers (regions 1, 100, 200: every keep, the relic keeps' main gates and the enemy portal keeps) must hold a bot
/// while either gate door is shut and let it through when both stand open. Every keep door of the region is registered
/// as the server registers it (LocalPathfindingMgr.RegisterDoor: 120 in a battleground, 64 elsewhere) and kept shut.
/// Before the fix bots walked under the gatehouse's raised floor past both gates at Caer Caledon, Caer Berkstead and the
/// Albion portal keep in Midgard, and round a shut gate on door slivers outside the 64 box at Glenlock Faste, Dun Bolg,
/// Dun nGed, Dun Abermenai and the Hibernia portal keeps of 250/251/253.
/// OFFLINE_DAOC_NAV_ROOT = a folder with lib\Detour.dll and navmesh-165\zoneNNN.nav (runtime\server once installed),
/// OFFLINE_DAOC_DB_PATH = a True 1.65 world database (Door, Keep, Zones, Mob rows; read only),
/// OFFLINE_DAOC_KEEPGATE_REGIONS = optional, e.g. "250,251,252,253" (default: 1,100,200,250,251,252,253).
/// </summary>
[TestFixture, Explicit("Read-only installed navmesh check for the keep gates"), NonParallelizable]
public class UT_BattlegroundKeepGateNavmesh165
{
    private static readonly DOL.Database.IObjectDatabase Empty =
        DispatchProxy.Create<DOL.Database.IObjectDatabase, DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
    private sealed class Server : GameServer { protected override DOL.Database.IObjectDatabase DataBaseImpl => Empty; }

    private sealed class Door : GameKeepDoor
    {
        public override int X { get; set; }
        public override int Y { get; set; }
        public override int Z { get; set; }
        public override Region CurrentRegion { get; set; }
        public override eDoorState State { get; set; } = eDoorState.Closed;
        public override bool IsAlive => true;
    }

    private static readonly Vector3 AlbionArrival = new(36428, 51260, 3952); // BattlegroundBrackets.BotArrival

    [Test]
    public void EveryKeepGateHoldsWhileShut()
    {
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        var failures = new List<string>();
        var previousServer = GameServer.Instance;
        var previousNav = PathfindingProvider.Instance;
        GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        bool mode = DOL.GS.ServerProperties.Properties.CLASSIC165_MODE;
        DOL.GS.ServerProperties.Properties.CLASSIC165_MODE = true; // navmesh-165 first, as the 1.65 server loads it
        try
        {
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            using var db = new SQLiteConnection($"Data Source={Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH")};Read Only=True;Pooling=False;");
            db.Open();
            var nav = PathfindingProvider.LocalPathfindingMgr;
            PathfindingProvider.SetPathfindingMgr(nav);
            var nodes = new WrappedPathfindingNode[2048];
            string regionList = Environment.GetEnvironmentVariable("OFFLINE_DAOC_KEEPGATE_REGIONS");
            ushort[] regionIds = string.IsNullOrWhiteSpace(regionList)
                ? new ushort[] { 1, 100, 200, 250, 251, 252, 253 }
                : regionList.Split(',').Select(r => ushort.Parse(r.Trim())).ToArray();
            int checkedKeeps = 0;
            foreach (ushort regionId in regionIds)
            {
                var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new RegionData { Id = regionId });
                typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone>());
                using (var q = db.CreateCommand())
                {
                    q.CommandText = $"select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID={regionId}";
                    using var r = q.ExecuteReader();
                    while (r.Read())
                    {
                        ushort zid = (ushort)r.GetInt32(0);
                        var zone = new Zone(region, zid, r.GetString(1), r.GetInt32(2) * 8192, r.GetInt32(3) * 8192, r.GetInt32(4) * 8192,
                            r.GetInt32(5) * 8192, zid, false, 0, false, 0, 0, 0, 0, 0);
                        region.Zones.Add(zone);
                        LocalPathfindingMgr.LoadNavMesh(zone);
                        loaded.Add(zone);
                    }
                }
                var keeps = new List<(string Name, Vector3 Point)>();
                using (var q = db.CreateCommand())
                {
                    q.CommandText = $"select Name, X, Y, Z from Keep where Region = {regionId}";
                    using var r = q.ExecuteReader();
                    while (r.Read()) keeps.Add((r.GetString(0), new(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3))));
                }
                var lords = new List<Vector3>();
                using (var q = db.CreateCommand())
                {
                    q.CommandText = $"select X, Y, Z from Mob where Region = {regionId} and ClassType like '%GuardLord%'";
                    using var r = q.ExecuteReader();
                    while (r.Read()) lords.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
                }
                // Every keep door of the region (realm 6 rows within 2,500 of a keep row; realm gates are
                // UT_GateNavmeshInstalled165's), registered and shut as the server keeps them until a raid breaks one.
                var doors = new List<Door>();
                using (var q = db.CreateCommand())
                {
                    q.CommandText = $"select d.InternalID, d.X, d.Y, d.Z, d.Heading from Door d join Zones z on z.ZoneID = d.InternalID / 1000000 " +
                                    $"where z.RegionID = {regionId} and d.Realm not in (1, 2, 3) order by d.InternalID";
                    using var r = q.ExecuteReader();
                    while (r.Read())
                    {
                        var p = new Vector3(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
                        if (!keeps.Any(k => Vector2.Distance(new(k.Point.X, k.Point.Y), new(p.X, p.Y)) <= 2500)) continue;
                        var door = (Door)RuntimeHelpers.GetUninitializedObject(typeof(Door));
                        door.CurrentRegion = region;
                        door.X = (int)p.X; door.Y = (int)p.Y; door.Z = (int)p.Z;
                        door.State = eDoorState.Closed;
                        door.DoorId = r.GetInt32(0);
                        door.Heading = (ushort)r.GetInt32(4);
                        if (door.CurrentZone == null || !nav.HasNavmesh(door.CurrentZone)) continue;
                        if (!nav.RegisterDoor(door)) failures.Add($"{door.DoorId}: no door polygons");
                        doors.Add(door);
                    }
                }
                foreach (var pair in doors.GroupBy(d => d.DoorId / 100).Where(g => g.Count(d => d.DoorId % 100 is 1 or 2) == 2))
                {
                    Door a = pair.First(d => d.DoorId % 100 == 1), b = pair.First(d => d.DoorId % 100 == 2);
                    Vector3 pa = new(a.X, a.Y, a.Z), pb = new(b.X, b.Y, b.Z), mid = (pa + pb) / 2;
                    var keep = keeps.OrderBy(k => Vector2.Distance(new(k.Point.X, k.Point.Y), new(mid.X, mid.Y))).First();
                    // The keep's lord when it has one near (the keep row centre stands in a different spot of each keep model),
                    // else its keep row centre (portal keeps, relic keeps).
                    Vector3? lord = lords.Where(l => Vector2.Distance(new(l.X, l.Y), new(keep.Point.X, keep.Point.Y)) <= 2500)
                        .Select(l => (Vector3?)l).FirstOrDefault();
                    Vector3 target = lord ?? keep.Point;
                    Door outer = Vector3.Distance(pa, target) > Vector3.Distance(pb, target) ? a : b, inner = outer == a ? b : a;
                    Vector3 po = new(outer.X, outer.Y, outer.Z), pi = new(inner.X, inner.Y, inner.Z);
                    Vector3 axis = Vector3.Normalize(new(po.X - pi.X, po.Y - pi.Y, 0));
                    Zone zone = outer.CurrentZone;
                    string name = $"r{regionId} z{zone.ID} {keep.Name} gates {outer.DoorId}/{inner.DoorId}";
                    Vector3? start = nav.GetClosestPoint(zone, po + axis * 700, 200, 200, 400, nav.DefaultFilters);
                    Vector3? end = nav.GetClosestPoint(zone, target, 96, 96, 256, nav.DefaultFilters);
                    if (start == null || end == null) { failures.Add($"{name}: no floor at the start or the target"); continue; }
                    PathfindingStatus Walk(Vector3 from, Vector3 to) => nav.GetPathStraight(zone, from, to, nav.BlockingDoorAvoidanceFilters, nodes).Status;
                    void Set(Door door, eDoorState state) { door.State = state; nav.UpdateDoorFlags(door); }
                    if (BattlegroundBrackets.IsBattlegroundRegion(regionId))
                    {
                        // the start stands in the open field: joined to the Albion arrival with every keep door shut
                        Vector3? field = nav.GetClosestPoint(zone, AlbionArrival, 200, 200, 400, nav.DefaultFilters);
                        if (field == null || Walk(field.Value, start.Value) != PathfindingStatus.PathFound)
                            failures.Add($"{name}: the start is not on the open field");
                    }
                    Set(outer, eDoorState.Open); Set(inner, eDoorState.Open);
                    PathfindingStatus open = Walk(start.Value, end.Value);
                    if (lord.HasValue && BattlegroundBrackets.IsBattlegroundRegion(regionId))
                    {
                        Vector3 breach = AutonomousWorldBotController.BattlegroundBreachPoint(
                            new Vector2(keep.Point.X, keep.Point.Y), pi, inner.Heading);
                        var floor = nav.GetClosestPoint(zone, breach, 48, 48, 96, nav.DefaultFilters);
                        if (!floor.HasValue || !AutonomousWorldBotController.IsRouteDestinationReached(floor.Value, breach) ||
                            Walk(start.Value, breach) != PathfindingStatus.PathFound ||
                            Walk(breach, end.Value) != PathfindingStatus.PathFound)
                            failures.Add($"{name}: assault checkpoint {breach} is not connected to both the field and lord");
                        else
                            TestContext.WriteLine($"BG BREACH {name}: field -> {breach} -> lord");
                    }
                    var shut = new Dictionary<string, PathfindingStatus>();
                    foreach ((string label, Door[] closed) in new[] { ("outer", new[] { outer }), ("inner", new[] { inner }), ("both", new[] { outer, inner }) })
                    {
                        Set(outer, eDoorState.Open); Set(inner, eDoorState.Open);
                        foreach (Door door in closed) Set(door, eDoorState.Closed);
                        shut[label] = Walk(start.Value, end.Value);
                    }
                    Set(outer, eDoorState.Closed); Set(inner, eDoorState.Closed);
                    TestContext.WriteLine($"KEEPGATE {name}: target={(lord.HasValue ? "lord" : "keep row")} {end.Value.X:0},{end.Value.Y:0},{end.Value.Z:0} " +
                                          $"bothOpen={open} outerShut={shut["outer"]} innerShut={shut["inner"]} bothShut={shut["both"]}");
                    if (open != PathfindingStatus.PathFound)
                    {
                        // A keep row centre behind further doors (relic keeps) is no target for this pair; a lord must be reached.
                        if (lord.HasValue) failures.Add($"{name}: no walk in with both gates open");
                        continue;
                    }
                    checkedKeeps++;
                    foreach (var kv in shut.Where(kv => kv.Value == PathfindingStatus.PathFound))
                        failures.Add($"{name}: a bot walks in with the {kv.Key} gate shut");
                }
            }
            TestContext.WriteLine($"KEEPGATE checked {checkedKeeps} keep gate pairs");
            if (checkedKeeps == 0) failures.Add("no keep gate pair checked: wrong OFFLINE_DAOC_NAV_ROOT / OFFLINE_DAOC_DB_PATH?");
        }
        finally
        {
            foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
            GameServer.LoadTestDouble(previousServer);
            DOL.GS.ServerProperties.Properties.CLASSIC165_MODE = mode;
            PathfindingProvider.SetPathfindingMgr(previousNav);
        }
        Assert.That(failures, Is.Empty, string.Join("\n", failures));
    }
}
