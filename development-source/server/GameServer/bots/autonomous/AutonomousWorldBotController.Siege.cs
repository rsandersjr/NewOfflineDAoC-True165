using System;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.GS.Keeps;
using DOL.GS.Spells;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        private string _siegeJobKeep;
        private BotSiegeKind _siegeKind;
        private int _siegeSlot;
        private long _siegeNextAttempt, _siegeNextPosition, _siegeLastHitLog, _siegeLastProgress, _siegeRepairUntil;
        private long _siegeObservedHits;
        private long _siegeRepairCombatTick;
        private Vector3? _siegePosition;
        private GameLiving _siegePositionTarget;
        private GameSiegeWeapon _siegeWeapon, _siegeRepair;
        private string _siegeSupplyItem;
        private int _siegeSupplyCount;
        private long _siegeSupplyStarted, _siegeNextTopup;
        private long _siegeNoTargetSince;
        private Vector3? _siegeMoveTo;
        private long _siegeMoveStarted, _siegeNextMove, _siegeMoveRepath;
        private string _siegePreSupplyKeep;

        public static long SiegeSupplyBudget(bool playerResponse) => playerResponse ? 180_000L : 600_000L;

        /// <summary>Siege crews get their engines on site (owner 2026-10-07: skips long shopping trips); the pre-march
        /// kit purchase is kept behind this switch.</summary>
        public const bool SyntheticSiegeEquipment = true;

        private bool PrepareSiegeResponseSupplies(GameBot bot, long now)
        {
            // Reserve against the destination, not the origin region: responders
            // from several towns must share one bounded equipment assignment pool.
            if (IsInFrontier(bot) || !_rvrSharedEvent || _rvrDestination == null ||
                _siegePreSupplyKeep == _rvrDestination.Id || now < _siegeNextAttempt ||
                !AutonomousSiegeJobs.Eligible(bot) ||
                !AutonomousRvrEventLayer.IsTargetActive(_rvrDestination.Id, now) || SyntheticSiegeEquipment) return false;
            var keep = GameServer.KeepManager.GetKeepsOfRegion(_rvrDestination.RegionId)
                .FirstOrDefault(k => _rvrDestination.Id == $"rvr-keep-{k.KeepID}" && AutonomousRvrKeepPolicy.IsSiegeObjective(k));
            if (keep == null) return false;
            if (_siegeJobKeep != null && _siegeJobKeep != _rvrDestination.Id) ReleaseSiegeJob(bot);
            bool attackingKeep = keep.Realm != bot.Realm;
            if (!AutonomousSiegeJobs.TryAcquire(bot, _rvrDestination.Id, 64, attackingKeep, !attackingKeep,
                out var kind, out var slot, _rvrDestination.RegionId, CarriedSiegeKind(bot)))
            { _siegeNextAttempt = now + 10_000; return false; }
            _siegeJobKeep = _siegePreSupplyKeep = _rvrDestination.Id;
            _siegeKind = kind; _siegeSlot = slot;
            SiegeLog(bot, "response_equipment", null, $"kind={kind} slot={slot}; other responders proceed independently");
            string kit = BotSiegeRuntime.Kit(bot.Realm, kind);
            // Already equipped operators proceed immediately. Repairs are topped
            // up opportunistically, never a prerequisite for marching to battle.
            return BotSiegeRuntime.Item(bot, kit) == null && SupplySiegeItem(bot, kit, kind == BotSiegeKind.Ram ? 2 : 1);
        }

        private static bool CanSupplySiege(GameBot bot)
        {
            if (BotSiegeRuntime.Item(bot,BotSiegeRuntime.Kit(bot.Realm,BotSiegeKind.Ram))!=null) return true;
            // True 1.65 Mode: no frontier siege merchants; a ram comes from a siegecrafter bot of the realm
            // (CrafterBots165.SupplySiege). Without this check no bot warband ever started a keep or relic assault.
            if (Classic165Mode.IsOn)
                return CrafterBots165.CanSupplySiege(bot, BotSiegeRuntime.Kit(bot.Realm, BotSiegeKind.Ram));
            var merchant=FindReachableSiegeMerchant(bot);
            long price=merchant?.TradeItems?.GetAllItems().Values.OfType<DbItemTemplate>()
                .Where(t=>t.Price>0 && AutonomousSiegePolicy.IsRamKit(bot.Realm,t.Id_nb)).Select(t=>t.Price).DefaultIfEmpty(0).Min() ?? 0;
            bool slot=bot.Inventory!=null && bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack,eInventorySlot.LastBackpack)!=eInventorySlot.Invalid;
            return AutonomousSiegePolicy.CanSupplyRam(false,AutonomousBotEconomy.GetMoney(bot.DatabaseID),price,slot);
        }

        private long _nextBattleAttendanceCheck;

        /// <summary>
        /// Keeps audit 2026-11-11 (L6): attendance at a running siege was reported only from the combat branch, and a ram
        /// operator, a ram crew or a bot battering a door leaves before it, so a siege that was only ramming could be
        /// called defended with its rams at work. Every attacker of a running battle within the fighting ring of the keep
        /// now reports itself present (ReportSiegeCombatAttendance: within 9,500 of the keep, every 2 s at most). Before
        /// the battle nothing changes: the rally posts still decide when it opens.
        /// </summary>
        private void ReportBattleAttendance(GameBot bot, long now)
        {
            if (!_rvrSharedEvent || _rvrDestination == null || now < _nextBattleAttendanceCheck ||
                bot.CurrentRegionID != _rvrDestination.RegionId) return;
            _nextBattleAttendanceCheck = now + 2_000 + bot.ObjectID % 500;
            string forceId = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
            if (AutonomousRvrEventLayer.IsBattleForce(forceId, now))
                ReportSiegeCombatAttendance(bot, now);
        }

        private bool TryRunSiegeJob(GameBot bot)
        {
            long now = GameLoop.GameLoopTime;
            ReportBattleAttendance(bot, now);
            if (TryRunSiegeJobCore(bot, now)) return true;
            return TryCrewRam(bot, now);
        }

        private bool TryRunSiegeJobCore(GameBot bot, long now)
        {
            if (_rvrIntent is not (AutonomousRvrEventLayer.Intent.AssaultKeep or AutonomousRvrEventLayer.Intent.AssaultRelicKeep or AutonomousRvrEventLayer.Intent.DefendEvent))
            { if (_siegeJobKeep!=null) ReleaseSiegeJob(bot); return false; }
            if (_siegeSupplyItem != null)
            {
                if (_siegeJobKeep != _rvrDestination?.Id) { ReleaseSiegeJob(bot); return false; }
                AutonomousSiegeJobs.Refresh(bot);
                return SupplySiegeItem(bot,_siegeSupplyItem,_siegeSupplyCount);
            }
            if (PrepareSiegeResponseSupplies(bot, now)) return true;
            if (_rvrDestination?.Id?.StartsWith("rvr-keep-", StringComparison.Ordinal) != true || !AutonomousSiegeJobs.Eligible(bot)) return false;
            if (_siegeJobKeep==_rvrDestination.Id && (bot.CurrentRegionID!=_rvrDestination.RegionId ||
                bot.GetDistanceTo(new Point3D(_rvrDestination.X,_rvrDestination.Y,_rvrDestination.Z))>4800))
            { AutonomousSiegeJobs.Refresh(bot); return TravelRvrObjective(bot,_rvrDestination); }
            if (bot.CurrentRegionID!=_rvrDestination.RegionId) return false;
            var keep = GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID).FirstOrDefault(k =>
                _rvrDestination.Id == $"rvr-keep-{k.KeepID}" && AutonomousRvrKeepPolicy.IsSiegeObjective(k));
            if (keep == null || bot.GetDistanceTo(new Point3D(keep.X, keep.Y, keep.Z)) > 6000) return false;
            bool attacking = keep.Realm != bot.Realm;
            GameKeepDoor door = attacking ? SiegeDoor(bot, keep) : null;
            if (_siegeJobKeep != _rvrDestination.Id)
            {
                ReleaseSiegeJob(bot);
                _siegeJobKeep = _rvrDestination.Id;
            }
            if (now < _siegeNextAttempt) return false;
            if (_siegeMoveTo.HasValue && _siegeWeapon != null)
                return ContinueSiegeMove(bot,now);
            var nearby = bot.GetNPCsInRadius(6000).ToArray();
            var engines = nearby.OfType<GameSiegeWeapon>().Where(w => w.IsAlive && w.ObjectState == GameObject.eObjectState.Active).ToArray();
            bool enemyEngines = engines.Any(w => BotSiegeRuntime.LegalEnemy(bot, w));
            int present = nearby.OfType<GameBot>().Count(b => b.IsAlive && b.Realm == bot.Realm) +
                bot.GetPlayersInRadius(6000).Count(p => p.IsAlive && p.Realm == bot.Realm);
            BotSiegeKind? carried = CarriedSiegeKind(bot);
            BotSiegeKind? reclaimable = AbandonedSiegeKind(bot, engines);
            int allowedKinds = -1;
            if (Classic165Mode.IsOn && attacking && BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID) &&
                BattlegroundSiegeMaster(bot) is { } master)
            {
                // A broke operator used to reserve the first free slot and then give it back.
                // Filter new leases using real coin and inventory; existing operators keep their leases.
                long purse = AutonomousBotEconomy.GetMoney(bot.DatabaseID);
                bool hasRoom = bot.Inventory?.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack)
                    is { } empty && empty != eInventorySlot.Invalid;
                allowedKinds = 0;
                foreach (BotSiegeKind kind in Enum.GetValues<BotSiegeKind>())
                {
                    string kit = BotSiegeRuntime.Kit(bot.Realm, kind);
                    DbItemTemplate offer = SoldBy(master, kit);
                    if (kind == reclaimable || BotSiegeRuntime.Item(bot, kit) != null ||
                        offer == null || (hasRoom && purse >= offer.Price))
                        allowedKinds |= 1 << (int)kind;
                }
            }
            if (!AutonomousSiegeJobs.TryAcquire(bot, _siegeJobKeep, present, attacking && door != null, enemyEngines, out _siegeKind, out _siegeSlot,
                    carried: carried ?? reclaimable, allowedKinds: allowedKinds))
            { _siegeNextAttempt = now + 10_000; return false; }
            if (now>=_siegeNextTopup)
            {
                _siegeNextTopup=now+30_000;
                var merchant=nearby.OfType<GameMerchant>().Where(m=>m.IsWithinRadius(bot,GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE))
                    .OrderBy(bot.GetDistanceTo).FirstOrDefault();
                int bought=BotSiegeRuntime.TryRestockRepairKits(bot,merchant);
                if (bought>0) SiegeLog(bot,"repair_restock",null,$"count={bought} target={BotSiegeRuntime.RepairRestockTarget}");
            }

            GameLiving target = SelectSiegeTarget(bot, keep, door, _siegeKind, nearby, engines);
            // Keep firing at an already valid target; changing cluster scores
            // must not continually restart aiming or move an effective engine.
            var currentWeapon = AutonomousSiegeOwnership.All(bot).FirstOrDefault();
            if (_siegeKind != BotSiegeKind.Ram && currentWeapon != null && BotSiegeRuntime.Kind(currentWeapon)==_siegeKind &&
                currentWeapon.TargetObject is GameLiving retained && BotSiegeRuntime.CanDamage(currentWeapon,retained) && InSiegeRange(currentWeapon,retained))
                target=retained;
            if (target == null)
            {
                if (_siegeNoTargetSince==0) _siegeNoTargetSince=now;
                // Keep the existing engine in place for brief gaps. Never continually
                // move a working engine after each target dies.
                if (now-_siegeNoTargetSince<60_000 && AutonomousSiegeOwnership.All(bot).FirstOrDefault() is { } waiting)
                { bot.StopMovingOnPath(); bot.StopMoving(); SiegeStatus(bot,"Holding siege position; no valid target",waiting); return true; }
                ReleaseSiegeJob(bot); _siegeNextAttempt=now+30_000; return false;
            }
            _siegeNoTargetSince=0;

            // Prioritize immediate personal defense without chasing distant enemies away from an engine.
            if (bot.GetNPCsInRadius(450).Any(n => n.IsAlive && n.TargetObject == bot && n.IsAttacking && SiegeOperatorThreat(bot, n)) ||
                bot.GetPlayersInRadius(450).Any(p => p.IsAttacking && p.TargetObject == bot && SiegeOperatorThreat(bot, p))) return false;

            GameSiegeWeapon owned = AutonomousSiegeOwnership.All(bot).FirstOrDefault();
            if (owned != null && (owned.CurrentRegion != bot.CurrentRegion || BotSiegeRuntime.Kind(owned) != _siegeKind)) { owned.ReleaseControl(); owned = null; }
            if (_siegeWeapon != null && !_siegeWeapon.IsAlive) SiegeLog(bot, "destroyed", _siegeWeapon, "replacement requested");
            _siegeWeapon = owned;
            if (owned == null)
            {
                GameSiegeWeapon abandoned = engines.Where(w => w.Realm == bot.Realm && w.Owner == null &&
                    BotSiegeRuntime.Kind(w) == _siegeKind && (w.Health > w.DecayedHp || w.TimesRepaired<=3 && BotSiegeRuntime.HasRepairMeans(bot,w)) && InSiegeRange(w, target))
                    .OrderBy(bot.GetDistanceTo).FirstOrDefault();
                if (abandoned != null)
                {
                    if (!bot.IsWithinRadius(abandoned, Math.Max(32, abandoned.SIEGE_WEAPON_CONTROLE_DISTANCE - 20)))
                    { IssuePath(bot, new(abandoned.X, abandoned.Y, abandoned.Z), preciseArrival:true); SiegeStatus(bot, "Reclaiming abandoned siege equipment", target); return true; }
                    bot.StopMovingOnPath(); bot.StopMoving();
                    if (abandoned.TryTakeControl(bot)) { owned = _siegeWeapon = abandoned; _siegeLastProgress = now; SiegeLog(bot, "reclaimed", owned, "native control acquired"); }
                }
            }
            if (owned != null && !InSiegeRange(owned, target))
            {
                // Preserve a legal current target instead of chasing a newly selected one.
                if (owned.TargetObject is GameLiving current && BotSiegeRuntime.LegalEnemy(bot,current) && InSiegeRange(owned,current)) target=current;
                else if (owned.EnableToMove && now >= _siegeNextMove && !owned.InCombat && !bot.InCombat)
                {
                    Vector3? move = ChooseSiegePosition(bot,target,_siegeKind,engines.Where(w=>w!=owned).ToArray(),keep,out _);
                    var nav=PathfindingProvider.Instance;
                    if (move.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,owned.CurrentZone,new(owned.X,owned.Y,owned.Z),move.Value))
                    {
                        owned.SiegeWeaponTimer.Stop(); owned.CurrentState=GameSiegeWeapon.eState.Inactive;
                        _siegeMoveTo=move; _siegeMoveStarted=now; _siegeNextMove=now+120_000; _siegeMoveRepath=0;
                        SiegeLog(bot,"relocating",owned,"no target in firing range; connected route verified");
                        return ContinueSiegeMove(bot,now);
                    }
                }
            }
            if (owned != null && !InSiegeRange(owned,target))
            {
                owned.ReleaseControl(); _siegeWeapon = null; _siegePosition = null;
                _siegeNextAttempt = now + 15_000;
                SiegeLog(bot, "released", owned, "target outside engine range");
                return false;
            }
            if (owned != null && owned.HealthPercent <= 60)
            {
                bot.StopMovingOnPath(); bot.StopMoving();
                if (TrySiegeRepair(bot, owned, now)) return true;
                if (owned.Health <= owned.DecayedHp)
                {
                    owned.ReleaseControl(); owned.TargetObject = null;
                    SiegeLog(bot, "unusable", owned, "no eligible safe repair; replace after cooldown");
                    _siegeWeapon = null; _siegeNextAttempt = now + 15_000; _siegePosition = null; return false;
                }
            }

            string kitId = BotSiegeRuntime.Kit(bot.Realm, _siegeKind);
            // Goal 1 (owner 2026-10-07): siege crews use a kit they carry, otherwise synthetic siege equipment set up on
            // the spot; no more shopping trips (runs to run 18: hundreds of supply timeouts, not one engine deployed).
            // True 1.65 Mode: no free engines: a kit the crew carries or buys from a siegecrafter of its realm (CrafterBots165).
            // In a battleground the crew walks to its realm's Siege Master at the portal keep and buys it there.
            // Only to deploy: an operator already working a usable engine (the kit was spent on it, or a reclaimed one) keeps
            // operating it and looks for a kit again once the engine is gone.
            if (NeedsNewKit(Classic165Mode.IsOn, owned != null, BotSiegeRuntime.Item(bot, kitId) != null))
            {
                // A Siege Master that does not sell the realm's kit: the siegecrafters, as before.
                if (SellsHere(BattlegroundSiegeMaster(bot), kitId)) return SupplySiegeItem(bot, kitId, 1);
                if (!CrafterBots165.SupplySiege(bot, kitId))
                {
                    SiegeStatus(bot, "Waiting for a siegecrafter's engine", target);
                    _siegeNextAttempt = now + 60_000;
                    return false;
                }
            }
            // A battleground crew with an artillery engine and no stones or bolts fetches them from its Siege Master (the
            // remote restock in SiegeAmmo165.Ready stays the frontier's way, and the fallback where no Siege Master stands).
            if (NeedsSiegeMasterAmmo(bot, owned, _siegeKind) is { } ammoId) return SupplySiegeItem(bot, ammoId, SiegeAmmo165.RestockTarget);
            // True 1.65 Mode: artillery fires only with ammunition (SiegeAmmo165; rams need none). The crew restocks when it
            // takes the job (muster time) and again after a supply run of RestockRetryMilliseconds; with none it leaves the
            // engine (ammunition still in it stays for another crew) and goes back to the door work of ordinary RvR.
            if (!SiegeAmmo165.Ready(bot, owned, _siegeKind, now))
            {
                SiegeStatus(bot, "Out of siege ammunition", target);
                SiegeLog(bot, "no_ammo", owned, $"kind={_siegeKind}; back to the door, restock in {SiegeAmmo165.RestockRetryMilliseconds / 1000}s");
                ReleaseSiegeJob(bot);
                _siegeNextAttempt = now + SiegeAmmo165.RestockRetryMilliseconds;
                return false;
            }
            bool synthetic = BotSiegeRuntime.Item(bot, kitId) == null;
            if (owned == null)
            {
                // Working player engines count too. Broken engines retain their native decay,
                // but do not count as functioning firepower and are never deleted here.
                int active = engines.Count(w => w.Realm == bot.Realm && w.Health > w.DecayedHp &&
                    (BotSiegeRuntime.Kind(w) == _siegeKind || _siegeKind is BotSiegeKind.Catapult or BotSiegeKind.Trebuchet && w is GameSiegeCatapult) &&
                    (w is not GameSiegeRam || w.IsWithinRadius(target, 600)));
                if (active >= 2) { _siegeNextAttempt = now + 10_000; return false; }
                if (_siegePositionTarget != target || _siegePosition == null)
                {
                    if (now < _siegeNextPosition) return false;
                    _siegeNextPosition = now + 15_000;
                    _siegePositionTarget = target;
                    _siegePosition = ChooseSiegePosition(bot, target, _siegeKind, engines, keep, out string blocked);
                    if (_siegePosition == null)
                    {
                        // Run 13 (2026-10-16): Dun Dagda's ram never found a spot (unconnected=34 of 35, 159 times) after the
                        // outer gate fell; which door, the gate's state and the bot's spot show where the approach breaks.
                        string doorInfo = target is GameKeepDoor kd
                            ? $" door={kd.DoorId} index={SiegeDoorNumber(kd)} at={kd.X},{kd.Y},{kd.Z} gate={string.Join("/", keep.Doors.Values.Where(d => SiegeDoorNumber(d) == 1).Select(d => $"{d.DoorId}:{(d.IsAlive ? "alive" : "broken")}:{d.State}"))}"
                            : "";
                        SiegeLog(bot, "placement_blocked", null, $"kind={_siegeKind} target={target.Name}{doorInfo} from={(int)bot.X},{(int)bot.Y},{(int)bot.Z} {blocked}");
                        _siegeNextAttempt = now + 15_000; return false;
                    }
                }
                if (Vector3.Distance(new(bot.X, bot.Y, bot.Z), _siegePosition.Value) > (_siegeKind == BotSiegeKind.Ballista ? 55 : 90))
                { IssuePath(bot, _siegePosition.Value, preciseArrival:true); SiegeStatus(bot, "Moving to verified siege position", target); return true; }
                bot.StopMovingOnPath(); bot.StopMoving();
                lock (AutonomousSiegeOwnership.ChangeGate)
                {
                    // Recheck the shared cap immediately before consuming any kit.
                    if (target.GetNPCsInRadius(_siegeKind == BotSiegeKind.Ram ? (ushort)650 : (ushort)6000)
                        .OfType<GameSiegeWeapon>().Count(w => w.IsAlive && w.Realm == bot.Realm && w.Health > w.DecayedHp &&
                            (BotSiegeRuntime.Kind(w) == _siegeKind || _siegeKind is BotSiegeKind.Catapult or BotSiegeKind.Trebuchet && w is GameSiegeCatapult)) >= 2) return false;
                    DbInventoryItem kit = synthetic ? null : BotSiegeRuntime.Item(bot, kitId);
                    SpellLine line = SkillBase.GetSpellLine(GlobalSpellsLines.Item_Effects);
                    Spell spell = kit == null || line == null ? null : SkillBase.FindSpell(kit.SpellID, line);
                    if (!synthetic && (spell == null || !ValidSiegeSpell(_siegeKind, spell.SpellType))) { _siegeNextAttempt = now + 30_000; return false; }
                    owned = CreateSiegeWeapon(_siegeKind);
                    // On the verified spot itself (the operator stops up to 90 short of it).
                    owned.CurrentRegion = bot.CurrentRegion;
                    owned.X = (int)_siegePosition.Value.X; owned.Y = (int)_siegePosition.Value.Y; owned.Z = (int)_siegePosition.Value.Z;
                    owned.Realm = bot.Realm; owned.ItemId = kit?.Id_nb;
                    // A new engine reports position (0,0) until it is in the world: add it, then check its range.
                    if (!owned.AddToWorld()) { SiegeLog(bot, "spawn_failed", null, "could not be added to the world"); return false; }
                    owned.Heading = owned.GetHeading(target);
                    if (!InSiegeRange(owned, target))
                    { SiegeLog(bot, "spawn_failed", null, $"out of range ({(int)owned.GetDistanceTo(target)})"); owned.Delete(); _siegePosition = null; return false; }
                    if (!owned.TryTakeControl(bot) || !synthetic && !BotSiegeRuntime.Consume(bot, kitId, 1))
                    { owned.Delete(); SiegeLog(bot, "spawn_failed", null, "operator could not take control"); return false; }
                }
                _siegeWeapon = owned; _siegeLastProgress = now; _siegeObservedHits = 0;
                SiegeLog(bot, "deployed", owned, synthetic ? "synthetic=true" : $"kit={kitId}");
            }
            if (!bot.IsWithinRadius(owned, Math.Max(32, owned.SIEGE_WEAPON_CONTROLE_DISTANCE - 20)))
            { IssuePath(bot, new(owned.X, owned.Y, owned.Z), preciseArrival:true); return true; }
            bot.StopMovingOnPath(); bot.StopMoving(); bot.TargetObject = target;
            if (attacking && owned is GameSiegeRam crewedRam) AutonomousRamCrews.Register(_siegeJobKeep, crewedRam);
            if (owned.TargetObject != target && !owned.SiegeWeaponTimer.IsAlive)
            { owned.TargetObject = target; owned.CurrentState &= ~GameSiegeWeapon.eState.Aimed; }
            if (!owned.SiegeWeaponTimer.IsAlive)
            {
                (owned as GameSiegeRam)?.UpdateRamStatus(); // crew at the ram shortens the reload
                if ((owned.CurrentState & GameSiegeWeapon.eState.Armed) == 0) owned.Arm();
                else if (SiegeAmmo165.Feed(bot, owned) != SiegeFeed.Ready) { } // True 1.65: ammunition in, LOAD (5 s)
                else if ((owned.CurrentState & GameSiegeWeapon.eState.Aimed) == 0) owned.Aim();
                else owned.Fire();
            }
            if (owned.ConfirmedHits != _siegeObservedHits)
            {
                _siegeObservedHits = owned.ConfirmedHits; _siegeLastProgress = now;
                AutonomousStuckWatchdog.MarkProgress(bot,eAutonomousProgressKind.SiegeParticipation);
                if (now >= _siegeLastHitLog) { SiegeLog(bot, "hit", owned, $"hits={owned.ConfirmedHits} targetHp={target.HealthPercent}"); _siegeLastHitLog = now + 60_000; }
            }
            if (now - _siegeLastProgress > 90_000)
            { SiegeLog(bot, "no_progress", owned, "90s without damage; release and retry"); owned.ReleaseControl(); _siegeNextAttempt = now + 30_000; return false; }
            SiegeStatus(bot, $"Siege {owned.CurrentState}: {owned.ShotsFired} shots / {owned.ConfirmedHits} confirmed hits", target);
            return true;
        }

        private static BotSiegeKind? CarriedSiegeKind(GameBot bot)
        {
            foreach (BotSiegeKind kind in new[] { BotSiegeKind.Ram, BotSiegeKind.Trebuchet, BotSiegeKind.Catapult, BotSiegeKind.Ballista })
                if (BotSiegeRuntime.Item(bot, BotSiegeRuntime.Kit(bot.Realm, kind)) != null) return kind;
            return null;
        }

        /// <summary>An attacker standing beside the operator at its own height (melee reach), not a wall archer.</summary>
        public static bool IsSiegeOperatorThreat(float horizontalDistance, float heightDifference) =>
            horizontalDistance <= 300 && Math.Abs(heightDifference) <= 160;

        private static bool SiegeOperatorThreat(GameBot bot, GameLiving attacker) =>
            IsSiegeOperatorThreat(Vector2.Distance(new(bot.X, bot.Y), new(attacker.X, attacker.Y)), attacker.Z - bot.Z);

        private static bool ValidSiegeSpell(BotSiegeKind kind, eSpellType type) => (kind, type) is
            (BotSiegeKind.Ram, eSpellType.SummonSiegeRam) or (BotSiegeKind.Catapult, eSpellType.SummonSiegeCatapult) or
            (BotSiegeKind.Trebuchet, eSpellType.SummonSiegeTrebuchet) or (BotSiegeKind.Ballista, eSpellType.SummonSiegeBallista);
        public static GameSiegeWeapon CreateSiegeWeapon(BotSiegeKind kind) => kind switch
        {
            // Heavy ram (reach 500, 200 base, 12 places): bot crews standing at it count as riders (GameSiegeRam.Crew).
            BotSiegeKind.Ram => new GameSiegeRam { Level = 3, Model = 2602, Name = "heavy siege ram" },
            BotSiegeKind.Catapult => new GameSiegeCatapult { Level = 3 },
            BotSiegeKind.Trebuchet => new GameSiegeTrebuchet { Level = 3 },
            _ => new GameSiegeBallista { Level = 3 }
        };
        public static bool InSiegeRange(GameSiegeWeapon w, GameLiving t) => t != null &&
            (w is GameSiegeRam ? w.GetDistanceTo(t) >= 200 && w.GetDistanceTo(t) <= w.attackComponent.AttackRange - 25 :
                w.GetDistanceTo(t) >= w.MinAttackRange + 50 && w.GetDistanceTo(t) <= w.MaxAttackRange - 100);

        /// <summary>
        /// A spot for this engine, following player siege practice (SiegePlacement): rams straight out from the door's
        /// outer face inside their reach; catapults and trebuchets on a firing ring around their ground target (no line
        /// of sight, the shot arcs), attackers on the far side from the keep and defenders inside; ballistas need a
        /// clear shot unless the target stands up on a wall. Every spot must be reachable without passing a closed
        /// enemy door. Run 18 and earlier: a navmesh line-of-sight ray to the door's centre (inside the excluded door
        /// polygon) and to wall tops failed every time, so no bot ever deployed an engine.
        /// </summary>
        internal static Vector3? ChooseSiegePosition(GameBot bot, GameLiving target, BotSiegeKind kind, GameSiegeWeapon[] engines,
            AbstractGameKeep keep, out string reason)
        {
            reason = "no navmesh";
            var nav = AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance,bot.CurrentRegion,bot.Realm);
            if (!nav.IsAvailable || !nav.HasNavmesh(bot.CurrentZone)) return null;
            Vector2 centre = new(keep.X, keep.Y);
            Vector3 at = new(target.X, target.Y, target.Z);
            var (min, max) = SiegePlacement.Range(kind);
            var candidates = kind == BotSiegeKind.Ram
                ? SiegePlacement.RamCandidates(at, target.Heading, centre, InnerDoorApproach(keep, target))
                : SiegePlacement.ArtilleryCandidates(at, centre, new(bot.X, bot.Y), min, max, keep.Realm != bot.Realm);
            int tried = 0, floorless = 0, band = 0, crowded = 0, blind = 0, uneven = 0, unconnected = 0;
            foreach (Vector3 raw in candidates.Take(kind == BotSiegeKind.Ram ? 35 : 80))
            {
                tried++;
                Vector3? floor = nav.GetClosestPoint(bot.CurrentZone, raw, 48, 48, kind==BotSiegeKind.Ram ? 256 : 1024, nav.DefaultFilters);
                if (!floor.HasValue) { floorless++; continue; }
                float distance = Vector3.Distance(floor.Value, at);
                if (kind == BotSiegeKind.Ram ? distance < min || distance > max : distance < min + 50 || distance > max - 100) { band++; continue; }
                // Only working engines take up a spot: wrecks left to their native decay blocked all 35 ram spots at Dun
                // Dagda's Relic Gate after the outer gate fell (run 23).
                if (engines.Any(w => w.Health > w.DecayedHp && Vector3.Distance(floor.Value, new(w.X, w.Y, w.Z)) < (kind == BotSiegeKind.Ram ? 120 : 400))) { crowded++; continue; }
                if (kind == BotSiegeKind.Ballista && target.Z - floor.Value.Z <= 150 &&
                    !nav.HasLineOfSight(bot.CurrentZone, floor.Value + new Vector3(0,0,48), at + new Vector3(0,0,48), nav.DefaultFilters)) { blind++; continue; }
                if (kind != BotSiegeKind.Ram && !LevelGround(nav, bot.CurrentZone, floor.Value)) { uneven++; continue; }
                if (!AutonomousZoneItinerary.HasCompleteCorridor(nav, bot.CurrentZone, new(bot.X, bot.Y, bot.Z), floor.Value)) { unconnected++; continue; }
                reason = null;
                return floor;
            }
            reason = $"tried={tried} floorless={floorless} out_of_reach={band} crowded={crowded} blind={blind} uneven={uneven} unconnected={unconnected}";
            return null;
        }

        // Artillery needs a small level pad (rams sit on the gate approach, often a ramp or bridge).
        private static bool LevelGround(IPathfindingMgr nav, Zone zone, Vector3 floor)
        {
            foreach (var d in new[] { new Vector3(40,0,0),new Vector3(-40,0,0),new Vector3(0,40,0),new Vector3(0,-40,0) })
            {
                Vector3? edge = nav.GetClosestPoint(zone, floor + d, 16,16,48,nav.DefaultFilters);
                if (!edge.HasValue || Math.Abs(edge.Value.Z-floor.Z)>56) return false;
            }
            return true;
        }

        /// <summary>The door a ram works on: the raid's next door by the one shared rule (KeepRaidDoors). Owner
        /// 2026-10-07: at Caer Sursbrooke a raid ram set up on the postern instead of the keep door; keeps audit 2026-11-11
        /// (L2): after both real gates fell the rams still went for that postern's unflagged half, and at relic keeps for
        /// the upper doors 07-09 that are not on the way to the relic. Door data is left as it is.</summary>
        internal static GameKeepDoor SiegeDoor(GameBot bot, AbstractGameKeep keep) => KeepRaidDoors.NextShutRaidGate(keep);

        /// <summary>
        /// Keeps audit 2026-11-11 (M1): spells do nothing to a keep door (SpellHandler), so a list caster is never sent to
        /// batter one; as players' casters did, it stays on the guards and defenders. Melee and archers batter.
        /// </summary>
        public static bool BattersDoorsByHand(bool listCaster) => !listCaster;

        /// <summary>
        /// With nothing else to fight, a sieging bot batters the raid's next door by hand when it stands within reach of
        /// it (owner 2026-10-31: players did), the same door the rams work on (keeps audit 2026-11-11, L2: the nearest
        /// shut door of any kind was taken, posterns' halves and relic doors off the way included).
        /// </summary>
        private GameKeepDoor FindRaidGateToBatter(GameBot bot)
        {
            if (_rvrIntent is not (AutonomousRvrEventLayer.Intent.AssaultKeep or AutonomousRvrEventLayer.Intent.AssaultRelicKeep) ||
                _rvrDestination?.Id.StartsWith("rvr-keep-", StringComparison.Ordinal) != true)
                return null;
            AbstractGameKeep keep = GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID)
                .FirstOrDefault(k => $"rvr-keep-{k.KeepID}" == _rvrDestination.Id);
            if (keep == null || keep.Realm == bot.Realm || !AutonomousRvrKeepPolicy.IsSiegeObjective(keep) ||
                !BattersDoorsByHand(bot.CharacterClass?.ClassType == eClassType.ListCaster)) return null;
            GameKeepDoor door = KeepRaidDoors.NextShutRaidGate(keep);
            return door != null && bot.IsWithinRadius(door, DoorBatterReach) && GameServer.ServerRules.IsAllowedToAttack(bot, door, true)
                ? door : null;
        }

        /// <summary>For a door behind the outer gate (the keep door), the gate's position: attackers reach it from that
        /// side. Null for the gate itself and for anything that is not a keep door.</summary>
        internal static Vector2? InnerDoorApproach(AbstractGameKeep keep, GameObject door) =>
            door is GameKeepDoor inner && SiegeDoorNumber(inner) != 1 &&
            keep.Doors.Values.FirstOrDefault(d => SiegeDoorNumber(d) == 1) is { } gate ? new Vector2(gate.X, gate.Y) : null;

        /// <summary>
        /// The door's number within its keep (the id's last two digits). GameKeepDoor.DoorIndex keeps only the last digit,
        /// so a relic keep's doors 10 and 11 read 0 and 1: run 14 (2026-10-16) Midgard's rams went for Dun Dagda's door 10
        /// ("index 0", by the keep centre) while the gate still stood, and found no reachable spot 159 times.
        /// </summary>
        public static int SiegeDoorNumber(GameKeepDoor door) => door.DoorId % 100;

        /// <summary>A postern, or the other half of one (within 200 of a flagged postern door).</summary>
        internal static bool PosternLike(AbstractGameKeep keep, GameKeepDoor door) => KeepRaidDoors.PosternLike(keep, door);

        /// <summary>
        /// What each engine shoots at: rams the door; trebuchets the same door (triple damage against doors);
        /// catapults the thickest knot of defenders (double damage against people, 150 splash), those fighting our ram
        /// crews first, wall archers included; ballistas enemy siege first, then a target in view or up on a wall.
        /// </summary>
        internal static GameLiving SelectSiegeTarget(GameBot bot, AbstractGameKeep keep, GameKeepDoor door, BotSiegeKind kind,
            GameNPC[] nearby, GameSiegeWeapon[] engines)
        {
            if (kind == BotSiegeKind.Ram) return door;
            if (kind == BotSiegeKind.Trebuchet && door != null) return door;
            var people = nearby.OfType<GameLiving>().Concat(bot.GetPlayersInRadius(5000)).Where(t =>
                    (BotPvpCrowdControl.PlayerLike(t) || t is GameKeepGuard guard && guard.Component?.Keep==keep && !guard.IsPortalKeepGuard && (door==null || guard is not GuardLord)) &&
                    BotSiegeRuntime.LegalEnemy(bot, t) && !BotPvpCrowdControl.Protected(bot, t))
                .OrderBy(bot.GetDistanceTo).Take(32).ToArray();
            if (kind == BotSiegeKind.Ballista)
                return engines.Where(w => BotSiegeRuntime.LegalEnemy(bot, w)).OrderBy(w => w is GameSiegeRam ? 0 : 1).ThenBy(bot.GetDistanceTo).FirstOrDefault()
                    ?? people.FirstOrDefault(t => t.Z - bot.Z > 150 || BotSiegeRuntime.Visible(bot, t));
            var rams = engines.Where(w => w is GameSiegeRam && w.Realm == bot.Realm && w.IsAlive).ToArray();
            return people.OrderByDescending(t => rams.Any(r => r.IsWithinRadius(t, 700)) ? 1 : 0)
                .ThenByDescending(t => people.Count(n => n.IsWithinRadius(t, 150)))
                .FirstOrDefault() ?? door;
        }

        /// <summary>A working friendly engine nobody is operating (a player's dropped ram included): its kind, so the bot
        /// takes that job and hops in.</summary>
        internal static BotSiegeKind? AbandonedSiegeKind(GameBot bot, GameSiegeWeapon[] engines) =>
            engines.Where(w => w.Realm == bot.Realm && w.Owner == null && w.IsAlive && w.Health > w.DecayedHp && bot.IsWithinRadius(w, 1500))
                .OrderBy(bot.GetDistanceTo).Select(w => (BotSiegeKind?)BotSiegeRuntime.Kind(w)).FirstOrDefault();

        private bool SupplySiegeItem(GameBot bot, string id, int quantity)
        {
            // Never leave a siege just to fetch repair supplies.
            if (id==BotSiegeRuntime.RepairKit) return false;
            long now=GameLoop.GameLoopTime;
            // Inside a battleground (both modes): the realm's own Siege Master at its portal keep.
            if (BattlegroundSiegeMaster(bot) is { } master && SellsHere(master, id)) return SupplyAtSiegeMaster(bot, master, id, quantity);
            if (_siegeSupplyItem!=id) _siegeSupplyStarted=now;
            // True 1.65 Mode: no siege merchants in the frontiers; siegecrafters supply the kits.
            if (Classic165Mode.IsOn)
            {
                bool got = CrafterBots165.SupplySiege(bot, id);
                if (!got) { ReleaseSiegeJob(bot); _siegeNextAttempt = now + 120_000; }
                else _siegeSupplyItem = null;
                return got;
            }
            bool urgent = AutonomousRvrEventLayer.IsPlayerDefenseResponse(_rvrDestination?.Id, now);
            if (now-_siegeSupplyStarted>SiegeSupplyBudget(urgent))
            { SiegeLog(bot,"supply_timeout",null,"return to ordinary RvR; bounded retry"); ReleaseSiegeJob(bot); _siegeNextAttempt=now+120_000; return false; }
            _siegeSupplyItem=id; _siegeSupplyCount=quantity;
            GameMerchant merchant = FindReachableSiegeMerchant(bot);
            if (merchant == null) { ReleaseSiegeJob(bot); _siegeNextAttempt = GameLoop.GameLoopTime + 60_000; return false; }
            DbItemTemplate template = merchant.TradeItems.GetAllItems().Values.OfType<DbItemTemplate>().FirstOrDefault(t => t.Id_nb == id && t.Price > 0);
            if (template == null || AutonomousBotEconomy.GetMoney(bot.DatabaseID) < template.Price * quantity)
            { ReleaseSiegeJob(bot); _siegeNextAttempt = GameLoop.GameLoopTime + 60_000; return false; }
            if (bot.CurrentRegionID != merchant.CurrentRegionID)
            {
                CampDestination previous = _camp;
                try { _camp = new($"siege-supply-{merchant.ObjectID}",merchant.Name,merchant.CurrentZone?.Description ?? "siege merchant",merchant.CurrentRegionID,merchant.X,merchant.Y,merchant.Z,1,false,false); return TravelAcrossRegions(bot); }
                finally { _camp=previous; }
            }
            if (!ApproachSupplyMerchant(bot, merchant)) { SiegeStatus(bot,"Buying siege supplies",merchant); return true; }
            lock (bot.Inventory)
            {
                eInventorySlot slot = bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack,eInventorySlot.LastBackpack);
                if (slot == eInventorySlot.Invalid) { ReleaseSiegeJob(bot); _siegeNextAttempt=GameLoop.GameLoopTime+60_000; return false; }
                long price=checked(template.Price*quantity);
                DbInventoryItem item=GameInventoryItem.Create(template);
                if (item==null) { ReleaseSiegeJob(bot); _siegeNextAttempt=GameLoop.GameLoopTime+60_000; return false; }
                item.Count=quantity;
                if (!AutonomousBotEconomy.TrySpend(bot.DatabaseID,price, BotMoneyLedger165.Kind.Siege)) return false;
                if (!bot.Inventory.AddItem(slot,item)) { AutonomousBotEconomy.AddMoney(bot.DatabaseID,price, BotMoneyLedger165.Kind.Siege); return false; }
                AutonomousBotEconomy.MarkInventoryChanged(bot); bot.MarkAutonomousStateDirty(); AutonomousBotStatusPersistence.Queue(bot,true);
                SiegeLog(bot,"purchase",null,$"item={id} count={quantity} copper={price}");
                _siegeSupplyItem=null;
            }
            int repairs=BotSiegeRuntime.TryRestockRepairKits(bot,merchant);
            if (repairs>0) SiegeLog(bot,"repair_restock",null,$"count={repairs} target={BotSiegeRuntime.RepairRestockTarget}");
            return true;
        }

        private bool TrySiegeRepair(GameBot bot, GameSiegeWeapon weapon, long now)
        {
            if (_siegeRepair != weapon) { _siegeRepair=weapon; _siegeRepairUntil=0; }
            if (!BotSiegeRuntime.CanRepair(bot,weapon))
            {
                _siegeRepairUntil=0;
                // Our last siege shot sets the ordinary combat timer. Pause
                // firing so it can clear before the timed repair begins.
                // Incoming attacks do not qualify for this safe wait.
                if (weapon.Owner==bot && weapon.IsAlive && weapon.TimesRepaired<=3 && !weapon.InCombat &&
                    !bot.IsCasting && !bot.IsIncapacitated && now-bot.LastAttackedByEnemyTick>10_000 &&
                    BotSiegeRuntime.HasRepairMeans(bot,weapon))
                { weapon.SiegeWeaponTimer.Stop(); SiegeStatus(bot,"Pausing fire before safe repair",weapon); return true; }
                return false;
            }
            long combatTick=Math.Max(Math.Max(bot.LastAttackTick,bot.LastAttackedByEnemyTick),Math.Max(weapon.LastAttackTick,weapon.LastAttackedByEnemyTick));
            if (_siegeRepairUntil==0 || _siegeRepairCombatTick!=combatTick)
            { _siegeRepairUntil=now+20_000; _siegeRepairCombatTick=combatTick; weapon.SiegeWeaponTimer.Stop(); }
            if (now<_siegeRepairUntil) { SiegeStatus(bot,"Repairing siege equipment",weapon); return true; }
            if (BotSiegeRuntime.RepairOnce(bot,weapon))
            { _siegeLastProgress=now; SiegeLog(bot,"repaired",weapon,$"health={weapon.HealthPercent} times={weapon.TimesRepaired}"); }
            else SiegeLog(bot,"repair_failed",weapon,$"health={weapon.HealthPercent}");
            _siegeRepairUntil=0; return true;
        }

        // ---- ram crews (keeps audit 2026-11-11, L5; AutonomousRamCrews)
        private Vector3? _crewSpot;
        private GameSiegeRam _crewSpotRam;
        private int _crewSpotSeat = -1;
        private long _crewNextLook, _crewRepairUntil;

        /// <summary>
        /// A melee fighter of the raid with no siege job takes a seat at the raid's ram with the most empty places, walks to
        /// its own spot behind the ram and stays there: its presence is what drives the ram (GameSiegeRam.Crew). It leaves the
        /// seat only to strike back at an enemy in melee reach, as the operator does; healers and the army's casters and
        /// archers protect it. A crew member with woodworking 301+ repairs the ram by the player rule while it is not under
        /// attack. Nothing here when the raid has no working ram at a raid door.
        /// </summary>
        private bool TryCrewRam(GameBot bot, long now)
        {
            if (_rvrIntent is not (AutonomousRvrEventLayer.Intent.AssaultKeep or AutonomousRvrEventLayer.Intent.AssaultRelicKeep) ||
                _rvrDestination?.Id?.StartsWith("rvr-keep-", StringComparison.Ordinal) != true ||
                bot.CurrentRegionID != _rvrDestination.RegionId || BotSiegeRuntime.Assigned(bot) ||
                GameRelic.IsPlayerCarryingRelic(bot) || !IsRamCrewCandidate(bot))
            {
                LeaveRamCrew(bot);
                return false;
            }
            GameSiegeRam ram = AutonomousRamCrews.KeepSeat(bot, out int seat);
            if (ram != null && (!AutonomousRamCrews.Working(ram, bot.Realm) || ram.TargetObject is not GameKeepDoor { IsAlive: true, State: eDoorState.Closed }))
            {
                LeaveRamCrew(bot);
                ram = null;
            }
            if (ram == null)
            {
                if (now < _crewNextLook ||
                    Distance(bot.X, bot.Y, _rvrDestination.X, _rvrDestination.Y) > 6_000) return false;
                _crewNextLook = now + 5_000;
                if (!AutonomousRamCrews.TryTakeSeat(bot, _rvrDestination.Id, out ram, out seat)) return false;
                SiegeLog(bot, "crew_seat", ram, $"seat={seat} crew={ram.Crew}");
            }
            // Only a blow at melee reach takes a crew member off the ram (the ordinary combat turn answers it).
            if (BotSiegeRuntime.StruckInMeleeReach(bot)) return false;
            if (bot.IsAttacking) bot.StopAttack();
            if (TryRepairCrewedRam(bot, ram, now)) return true;
            if (_crewSpotRam != ram || _crewSpotSeat != seat || !_crewSpot.HasValue)
            {
                _crewSpotRam = ram; _crewSpotSeat = seat;
                GameObject door = ram.TargetObject ?? ram;
                Vector2 spot = AutonomousRamCrews.CrewSpot(new(ram.X, ram.Y), new(door.X, door.Y), seat);
                var nav = PathfindingProvider.Instance;
                Vector3 raw = new(spot.X, spot.Y, ram.Z);
                Vector3? floor = nav.IsAvailable && nav.HasNavmesh(ram.CurrentZone)
                    ? nav.GetClosestPoint(ram.CurrentZone, raw, 48, 48, 128, nav.DefaultFilters) : raw;
                _crewSpot = floor.HasValue && Vector2.Distance(new(floor.Value.X, floor.Value.Y), new(ram.X, ram.Y)) <= 280
                    ? floor.Value : new Vector3(ram.X, ram.Y, ram.Z);
            }
            if (Vector3.Distance(new(bot.X, bot.Y, bot.Z), _crewSpot.Value) > 60 && !(bot.IsWithinRadius(ram, 280) && !bot.IsMoving &&
                    Vector3.Distance(new(bot.X, bot.Y, bot.Z), _crewSpot.Value) <= 160))
            {
                IssuePath(bot, _crewSpot.Value, preciseArrival: true);
                SiegeStatus(bot, "Joining the ram crew", ram);
                return true;
            }
            bot.StopMovingOnPath(); bot.StopMoving();
            if (ram.TargetObject != null) bot.TurnTo(ram.TargetObject);
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.SiegeParticipation);
            SiegeStatus(bot, $"Crewing the ram: {ram.Crew} of {ram.MAX_PASSENGERS} places", ram);
            return true;
        }

        /// <summary>Who crews a ram: melee fighters (AutonomousRamCrews.IsCrewCandidate).</summary>
        private static bool IsRamCrewCandidate(GameBot bot) => bot.CharacterClass != null &&
            AutonomousRamCrews.IsCrewCandidate(AutonomousRvrDefense.IsRangedDefender(bot),
                BotPartyRoles.For((eCharacterClass)bot.CharacterClass.ID) == BotPartyRole.Support ||
                (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Shaman);

        /// <summary>A crew member who can repair a siege engine by the player rule keeps its decaying ram working: 20 seconds
        /// standing at it, not while it or the ram is under attack.</summary>
        private bool TryRepairCrewedRam(GameBot bot, GameSiegeRam ram, long now)
        {
            if (ram.HealthPercent > 70 || !BotSiegeRuntime.HasRepairMeans(bot, ram) || ram.InCombat || bot.InCombat)
            { _crewRepairUntil = 0; return false; }
            if (!bot.IsWithinRadius(ram, WorldMgr.INTERACT_DISTANCE - 20))
            {
                _crewRepairUntil = 0;
                IssuePath(bot, new(ram.X, ram.Y, ram.Z), preciseArrival: true);
                SiegeStatus(bot, "Going to repair the ram", ram);
                return true;
            }
            bot.StopMovingOnPath(); bot.StopMoving();
            if (_crewRepairUntil == 0) _crewRepairUntil = now + 20_000;
            if (now < _crewRepairUntil) { SiegeStatus(bot, "Repairing the ram", ram); return true; }
            _crewRepairUntil = 0;
            SiegeLog(bot, BotSiegeRuntime.RepairOnce(bot, ram) ? "crew_repaired" : "crew_repair_failed", ram,
                $"health={ram.HealthPercent} times={ram.TimesRepaired}");
            _crewSpot = null;
            return true;
        }

        private void LeaveRamCrew(GameBot bot)
        {
            AutonomousRamCrews.Leave(bot);
            _crewSpot = null; _crewSpotRam = null; _crewSpotSeat = -1; _crewRepairUntil = 0;
        }
        private void ReleaseSiegeJob(GameBot bot)
        {
            AutonomousSiegeJobs.Release(bot);
            foreach(var w in AutonomousSiegeOwnership.All(bot)) w.ReleaseControl();
            _siegeJobKeep=null; _siegeWeapon=null; _siegePosition=null; _siegeRepair=null; _siegeRepairUntil=0;
            _siegeSupplyItem=null; _siegeNoTargetSince=0;
            _siegeMoveTo=null;
        }
        private bool ContinueSiegeMove(GameBot bot,long now)
        {
            var weapon=_siegeWeapon;
            AutonomousSiegeJobs.Refresh(bot);
            if (weapon?.IsAlive!=true || weapon.Owner!=bot || bot.InCombat || weapon.InCombat || now-_siegeMoveStarted>180_000)
            {
                weapon?.StopMovingOnPath(); weapon?.StopMoving(); _siegeMoveTo=null; _siegeNextAttempt=now+15_000;
                SiegeLog(bot,"relocation_stopped",weapon,"combat, control loss or bounded timeout"); return false;
            }
            if (Vector3.Distance(new(weapon.X,weapon.Y,weapon.Z),_siegeMoveTo.Value)<40)
            { weapon.StopMovingOnPath(); weapon.StopMoving(); _siegeMoveTo=null; _siegeLastProgress=now; return true; }
            // Move in short native path segments while the operator stays within
            // control range. No teleports and no through-wall direct WalkTo.
            if (!bot.IsWithinRadius(weapon,30)) { weapon.StopMovingOnPath(); weapon.StopMoving(); }
            else if (now>=_siegeMoveRepath)
            { weapon.PathTo(_siegeMoveTo.Value,20); _siegeMoveRepath=now+3000; }
            if (!bot.IsWithinRadius(weapon,15))
                bot.PathTo(new Vector3(weapon.X,weapon.Y,weapon.Z),bot.MaxSpeed);
            SiegeStatus(bot,"Relocating siege equipment along checked route",weapon); return true;
        }
        // ---- battleground Siege Masters (battlegrounds fix 2026-11-12, owner: "put them back. make the bots use them.")
        // 1.53D (FH 97993, 2002-09-12): a Siege Master "at your realm's portal keep within the Battlegrounds"; FH 116313
        // (2003-03-02, Caledonia): "tried about 4 rams on it from the siege master". A siege crew inside a battleground buys
        // its kit, and the stones or bolts its engine fires, from its own realm's Siege Master there, at the list price, and
        // carries them back to the keep, as a player did. Where no Siege Master of the realm stands in that battleground,
        // and everywhere outside the battlegrounds, nothing changes (True 1.65: the siegecrafters and the merchant-price
        // ammunition restock; normal mode: the nearest siege merchant). Cost: one scan of the region's merchants per realm
        // and battleground every five minutes; a crew that cannot pay looks again after five minutes.

        public const long SiegeMasterCacheMilliseconds = 5 * 60_000;
        public const long SiegeMasterRetryMilliseconds = 5 * 60_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort Region, eRealm Realm), (long Until, GameMerchant Master)> SiegeMasters = new();

        /// <summary>The bot's realm's Siege Master in the battleground it stands in (the one nearest its realm's arrival
        /// point, so at its own portal keep); null outside a battleground or where none stands.</summary>
        internal static GameMerchant BattlegroundSiegeMaster(GameBot bot)
        {
            if (bot?.CurrentRegion == null || !BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID)) return null;
            long now = GameLoop.GameLoopTime;
            var key = (bot.CurrentRegionID, bot.Realm);
            if (SiegeMasters.TryGetValue(key, out var cached) && now < cached.Until &&
                (cached.Master == null || cached.Master.ObjectState == GameObject.eObjectState.Active))
                return cached.Master;
            GameLocation home = BattlegroundBrackets.ForRegion(bot.CurrentRegionID) is { } bracket
                ? BattlegroundBrackets.BotArrival(bracket, bot.Realm) : null;
            Vector2 anchor = home != null ? new(home.X, home.Y) : new(bot.X, bot.Y);
            GameMerchant master = null;
            float best = float.MaxValue;
            foreach (GameMerchant merchant in bot.CurrentRegion.Merchants)
            {
                if (merchant == null || merchant.ObjectState != GameObject.eObjectState.Active || merchant.TradeItems == null ||
                    !IsSiegeMasterFor(bot.Realm, merchant.Realm, merchant.TradeItems.ItemsListID)) continue;
                float distance = Vector2.DistanceSquared(anchor, new(merchant.X, merchant.Y));
                if (distance < best) { best = distance; master = merchant; }
            }
            SiegeMasters[key] = (now + SiegeMasterCacheMilliseconds, master);
            return master;
        }

        /// <summary>Pure rule: a merchant serves a realm's siege crews when it sells that realm's siege list and is of that
        /// realm or of none.</summary>
        public static bool IsSiegeMasterFor(eRealm realm, eRealm merchantRealm, string itemListId) =>
            (merchantRealm == eRealm.None || merchantRealm == realm) && AutonomousSiegePolicy.IsSiegeMerchant(realm, itemListId);

        /// <summary>Pure rule: how many a crew buys at the Siege Master: a kit only when it can pay for all of them,
        /// ammunition up to the wanted stack as far as its coin goes (SiegeAmmo165.RestockCount).</summary>
        public static int SiegeMasterPurchaseCount(bool ammunition, int held, int wanted, long money, long price) =>
            price <= 0 || wanted <= 0 ? 0
            : ammunition ? SiegeAmmo165.RestockCount(held, money, price, wanted)
            : money / price >= wanted ? wanted : 0;

        /// <summary>The engine kind a kit deploys for this realm (null for anything else).</summary>
        public static BotSiegeKind? KitKind(eRealm realm, string id)
        {
            foreach (BotSiegeKind kind in Enum.GetValues<BotSiegeKind>())
                if (string.Equals(BotSiegeRuntime.Kit(realm, kind), id, StringComparison.OrdinalIgnoreCase)) return kind;
            return null;
        }

        /// <summary>The ammunition a battleground crew must fetch from its Siege Master before it can fire: True 1.65,
        /// an engine kind that fires it, none in the engine or the pack, and a Siege Master of the realm here.</summary>
        private static string NeedsSiegeMasterAmmo(GameBot bot, GameSiegeWeapon engine, BotSiegeKind kind)
        {
            if (!SiegeAmmo165.Required(kind) || engine != null && BotSiegeRuntime.Kind(engine) == kind && engine.HasAmmo ||
                SiegeAmmo165.Carried(bot, kind) > 0) return null;
            string ammoId = SiegeAmmo165.AmmoId(bot.Realm, SiegeAmmo165.BasicAmmo(kind));
            // A Siege Master whose list lacks the realm's stones or bolts: the ordinary restock (SiegeAmmo165.Ready: crafted,
            // a siegecrafter bot, or the list price), as in the frontier, instead of a give-up every five minutes.
            return SellsHere(BattlegroundSiegeMaster(bot), ammoId) ? ammoId : null;
        }

        /// <summary>The bot's battleground Siege Master exists and sells this item (kit or ammunition).</summary>
        private static bool SellsHere(GameMerchant master, string id) => master != null && SoldBy(master, id) != null;

        /// <summary>
        /// Pure rule: a siege crew looks for a new kit only to deploy an engine, never while it works a usable one (the kit
        /// was spent on it, or it reclaimed an abandoned one). Before (pre-existing, review 2026-11-12) a frontier crew at
        /// its deployed engine with no spare kit stood "Waiting for a siegecrafter's engine", or bought a spare.
        /// </summary>
        public static bool NeedsNewKit(bool classic165, bool workingEngine, bool carriesKit) =>
            classic165 && !workingEngine && !carriesKit;

        private static DbItemTemplate SoldBy(GameMerchant merchant, string id) => id == null ? null :
            merchant?.TradeItems?.GetAllItems().Values.OfType<DbItemTemplate>()
                .FirstOrDefault(t => t.Price > 0 && string.Equals(t.Id_nb, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>One step of a crew's errand to its Siege Master: walk there, pay, carry it back (TryRunSiegeJobCore
        /// then walks the bot to the keep). True while the errand runs or when it is done; false when it gave up.</summary>
        private bool SupplyAtSiegeMaster(GameBot bot, GameMerchant master, string id, int quantity)
        {
            long now = GameLoop.GameLoopTime;
            if (_siegeSupplyItem != id) _siegeSupplyStarted = now;
            if (now - _siegeSupplyStarted > SiegeSupplyBudget(false))
                return GiveUpSiegeMaster(bot, master, id, "gave_up", now);
            bool ammunition = SiegeAmmo165.KindOf(id) != SiegeAmmoKind.None;
            DbItemTemplate template = SoldBy(master, id);
            int held = ammunition ? bot.Inventory?.CountItemTemplate(template?.Id_nb ?? id, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) ?? 0 : 0;
            int count = template == null ? 0 :
                SiegeMasterPurchaseCount(ammunition, held, ammunition ? quantity : 1, AutonomousBotEconomy.GetMoney(bot.DatabaseID), template.Price);
            if (count <= 0) return GiveUpSiegeMaster(bot, master, id, template == null ? "not_sold" : "cannot_pay", now);
            _siegeSupplyItem = id; _siegeSupplyCount = quantity;
            if (!ApproachSupplyMerchant(bot, master))
            {
                SiegeStatus(bot, $"Buying siege supplies from {master.Name}", master);
                return true;
            }
            if (!BuyFromSiegeMaster(bot, template, count)) return GiveUpSiegeMaster(bot, master, id, "pack_full", now);
            SiegeLog(bot, "purchase", null, $"item={template.Id_nb} count={count} copper={template.Price * count} from=\"{master.Name}\" battleground=True");
            // A kit for an engine that fires ammunition: its stones or bolts in the same visit.
            if (!ammunition && KitKind(bot.Realm, id) is { } kind && SiegeAmmo165.Required(kind) &&
                SoldBy(master, SiegeAmmo165.AmmoId(bot.Realm, SiegeAmmo165.BasicAmmo(kind))) is { } ammo)
            {
                int have = bot.Inventory.CountItemTemplate(ammo.Id_nb, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                int more = SiegeMasterPurchaseCount(true, have, SiegeAmmo165.RestockTarget, AutonomousBotEconomy.GetMoney(bot.DatabaseID), ammo.Price);
                if (more > 0 && BuyFromSiegeMaster(bot, ammo, more))
                    SiegeLog(bot, "purchase", null, $"item={ammo.Id_nb} count={more} copper={ammo.Price * more} from=\"{master.Name}\" battleground=True");
            }
            _siegeSupplyItem = null;
            return true;
        }

        private bool GiveUpSiegeMaster(GameBot bot, GameMerchant master, string id, string why, long now)
        {
            SiegeLog(bot, "siege_master_" + why, null, $"item={id} master=\"{master.Name}\" retrySeconds={SiegeMasterRetryMilliseconds / 1000}");
            ReleaseSiegeJob(bot);
            _siegeNextAttempt = now + SiegeMasterRetryMilliseconds;
            return false;
        }

        /// <summary>A real purchase: the coin leaves first (the list price, as GameMerchant charges) and comes back if the
        /// pack refuses the goods.</summary>
        private static bool BuyFromSiegeMaster(GameBot bot, DbItemTemplate template, int count)
        {
            if (bot?.Inventory == null || template == null || count <= 0) return false;
            long price = checked(template.Price * count);
            if (!AutonomousBotEconomy.TrySpend(bot.DatabaseID, price, BotMoneyLedger165.Kind.Siege)) return false;
            if (SiegeAmmo165.Give(bot, template, count)) return true;
            AutonomousBotEconomy.AddMoney(bot.DatabaseID, price, BotMoneyLedger165.Kind.Siege);
            return false;
        }

        private void SiegeStatus(GameBot bot,string status,GameLiving target) => SetRvrStatus(bot,status,target.Name,"Real siege equipment, finite inventory and native combat timers");
        private void SiegeLog(GameBot bot,string action,GameSiegeWeapon weapon,string detail)
        { if(Log.IsInfoEnabled) Log.Info($"RVR_SIEGE action={action} bot={bot.Name} keep={_siegeJobKeep} engine={weapon?.ObjectID} {detail}"); }
    }
}
