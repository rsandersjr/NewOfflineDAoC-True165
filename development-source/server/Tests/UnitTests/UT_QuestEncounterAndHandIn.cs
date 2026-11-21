using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_QuestEncounterAndHandIn
    {
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, Database>();
        public class Database : UT_UnobservedConcentration.EmptyReads
        {
            protected override object Invoke(MethodInfo method, object[] args) =>
                method.Name == "SaveObject" ? true : base.Invoke(method, args);
        }
        private GameServer _previous;
        private PetTestLanguageScope _language;
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public IPacketLib Packets;
            public GameClient TestClient;
            public override GameClient Client => TestClient;
            public override IPacketLib Out => Packets;
            public override ICharacterClass CharacterClass => new DOL.GS.PlayerClass.ClassNaturalist();
            public override string RaceName => "Celt";
            public override string RealmTitle => "Guardian";
            public ConcurrentDictionary<AbstractQuest, byte> Quests = new();
            public override ConcurrentDictionary<AbstractQuest, byte> QuestList => Quests;
            public override byte Level { get => 10; set { } }
            public override ushort CurrentRegionID { get => 200; set { } }
            public override string Name { get => "quest-player"; set { } }
            public override int X { get => 0; set { } }
            public override int Y { get => 0; set { } }
            public override int Z { get => 0; set { } }
        }
        public class Packets : DispatchProxy
        {
            public readonly List<string> Messages = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SendMessage") Messages.Add((string)args[0]);
                return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }
        private sealed class Npc : GameNPC
        {
            public override ushort CurrentRegionID { get => 200; set { } }
            public override eRealm Realm { get; set; }
            public override int X { get => 0; set { } }
            public override int Y { get => 0; set { } }
            public override int Z { get => 0; set { } }
            public void AddOffer(DataQuest quest) => (_dataQuests ??= new()).Add(quest);
        }
        public class Inventory : DispatchProxy
        {
            public DbInventoryItem EquippedTestItem;
            public DbInventoryItem Removed;
            public int RemovalCount;
            private readonly System.Threading.Lock _lock = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_Lock") return _lock;
                if (method.Name == "GetFirstItemByID" && EquippedTestItem != null &&
                    (string)args[0] == EquippedTestItem.Id_nb &&
                    EquippedTestItem.SlotPosition >= (int)(eInventorySlot)args[1] &&
                    EquippedTestItem.SlotPosition <= (int)(eInventorySlot)args[2])
                    return EquippedTestItem;
                if (method.Name == "RemoveItem")
                {
                    Removed = (DbInventoryItem)args[0];
                    RemovalCount++;
                    Removed.OwnerID = null;
                    return true;
                }
                return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }
        private sealed class Quest : DataQuest
        {
            public bool BlockAdvance;
            public int OfferedHandIns;
            public Quest(DbDataQuest row) : base(row) { }
            public Quest(GamePlayer p, DbDataQuest row, short step) : base(p, row, new DbCharacterXDataQuest { Step = step }) { }
            public void Receive(GamePlayer p, GameObject target, DbInventoryItem item) =>
                Notify(GameLivingEvent.ReceiveItem, target, new ReceiveItemEventArgs(p, target, item));
            public void HandIn(GamePlayer p, GameObject target, DbInventoryItem item) =>
                Notify(GamePlayerEvent.GiveItem, p, new GiveItemEventArgs(p, target, item));
            public void Interact(GamePlayer p, GameObject target) => OnPlayerInteract(p, target);
            protected override bool ExecuteCustomQuestStep(GamePlayer player, int step, eStepCheckType type) =>
                !(BlockAdvance && type == eStepCheckType.Step) && base.ExecuteCustomQuestStep(player, step, type);
            protected override void CheckOfferedQuestReceiveItem(GamePlayer player, GameObject obj, DbInventoryItem item) => OfferedHandIns++;
            protected override void TryTurnTo(GameObject target, GamePlayer player) { }
        }
        [SetUp]
        public void Setup()
        {
            DOL.Logging.LoggerManager.InitializeWithExplicitLibrary(null, DOL.Logging.LogLibrary.None);
            _previous = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            _language = new PetTestLanguageScope();
        }
        [TearDown]
        public void Teardown()
        {
            _language.Dispose();
            GameServer.LoadTestDouble(_previous);
        }
        private static Npc CreateNpc(string name)
        {
            var npc = (Npc)RuntimeHelpers.GetUninitializedObject(typeof(Npc));
            npc.Name = name;
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(npc, new PropertyCollection());
            return npc;
        }
        private static Player CreatePlayer()
        {
            var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            p.Quests = new();
            p.Packets = DispatchProxy.Create<IPacketLib, Packets>();
            p.TestClient = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
            p.TestClient.Account = new DbAccount { Language = "EN", PrivLevel = 1 };
            var inv = DispatchProxy.Create<IGameInventory, Inventory>();
            typeof(GameLiving).GetField("m_inventory", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(p, inv);
            return p;
        }
        [Test]
        public void SavedQuestAtAxeStepAdvancesOnlyAfterEquippingExistingAxe()
        {
            var player = CreatePlayer();
            var inventory = (Inventory)player.Inventory;
            var axe = GameInventoryItem.Create(new DbItemTemplate { Id_nb = "cq_ancient_axe", Name = "Ancient Axe" });
            axe.SlotPosition = (int)eInventorySlot.FirstBackpack;
            inventory.EquippedTestItem = axe;
            var quest = new Quest(player, new DbDataQuest
            {
                ID = 21054, Name = "Thane's Blood", StartRegionID = 200,
                StepType = "4|0|8|4|5",
                StepText = "get charm|wear charm|wield axe|find smith|return",
                TargetName = "Dahn;200|Seer;200||Dverge;200|Gythja;200"
            }, 3);
            var step = new ClassicQuests.StepInfo
            {
                Custom = new ClassicQuests.CustomStep { Kind = "equip_item", Item = axe.Id_nb, Give = true }
            };
            var update = typeof(ClassicQuests).GetMethod("CheckCustomStep", BindingFlags.NonPublic | BindingFlags.Static);

            update.Invoke(null, new object[] { player, quest, step });
            Assert.That(quest.Step, Is.EqualTo(3));
            axe.SlotPosition = (int)eInventorySlot.RightHandWeapon;
            update.Invoke(null, new object[] { player, quest, step });
            Assert.That(quest.Step, Is.EqualTo(4));
            Assert.That(inventory.RemovalCount, Is.Zero);
        }

        private static Quest TroyaQuest(Player p, short step = 2)
        {
            var q = new Quest(p, new DbDataQuest
            {
                ID = 20060, Name = "Troya's Research", StartName = "Troya", StartRegionID = 200,
                StepType = "0|10|0|11", StepText = "skin|return skin|pawn|return tibia",
                StepItemTemplates = "cq_larva_skin;60||cq_tibia;60|",
                CollectItemTemplate = "|cq_larva_skin||cq_tibia",
                TargetName = "water beetle larva;200|Troya;200|skeletal pawn;200|Troya;200",
                SourceText = "|||", TargetText = "|Ah, the skin! Now bring a tibia.||", RewardMoney = "0|0|0|0", RewardXP = "0|0|0|0",
                ClassType = "DOL.GS.Quests.ClassicQuestStep"
            }, step);
            p.Quests.TryAdd(q, 0);
            return q;
        }
        private static DbInventoryItem Skin(string id = "cq_larva_skin")
        {
            var item = GameInventoryItem.Create(new DbItemTemplate { Id_nb = id, Name = "Larva Skin", MaxCount = 1 });
            item.OwnerID = "quest-player";
            item.Count = 1;
            return item;
        }
        [Test]
        public void TroyaTakesSkinBeforeThePawnStep()
        {
            var p = CreatePlayer();
            var q = TroyaQuest(p);
            var item = Skin();
            // The NPC holds an offer-only copy. Receiving the item must reach the active quest.
            new Quest(q.DBDataQuest).Receive(p, CreateNpc("Troya"), item);
            Assert.That(q.Step, Is.EqualTo(3));
            Assert.That(((Inventory)(object)p.Inventory).Removed, Is.SameAs(item));
            Assert.That(((Packets)(object)p.Packets).Messages, Does.Contain("Ah, the skin! Now bring a tibia."));
        }
        [Test]
        public void GiveThenReceiveDoesNotTakeTheSkinOrAdvanceTwice()
        {
            var p = CreatePlayer();
            var q = TroyaQuest(p);
            var troya = CreateNpc("Troya");
            var item = Skin();
            q.HandIn(p, troya, item);
            var offer = new Quest(q.DBDataQuest);
            offer.Receive(p, troya, item);
            offer.Receive(p, troya, item); // GameNPC and GameObject both forward receive-item.
            Assert.That(q.Step, Is.EqualTo(3));
            Assert.That(((Inventory)(object)p.Inventory).RemovalCount, Is.EqualTo(1));
        }
        [TestCase("Troya", "unrelated_skin", 2)]
        [TestCase("Other NPC", "cq_larva_skin", 2)]
        [TestCase("Troya", "cq_larva_skin", 1)]
        public void ReceivingDoesNotBypassTargetItemOrStepChecks(string npc, string id, short step)
        {
            var p = CreatePlayer();
            var q = TroyaQuest(p, step);
            var item = Skin(id);
            new Quest(q.DBDataQuest).Receive(p, CreateNpc(npc), item);
            Assert.That(q.Step, Is.EqualTo(step));
            Assert.That(item.OwnerID, Is.Not.Null);
            Assert.That(((Inventory)(object)p.Inventory).RemovalCount, Is.Zero);
        }
        [Test]
        public void BlockedHandInDoesNotTakeTheSkinOrThankThePlayer()
        {
            var p = CreatePlayer();
            var q = TroyaQuest(p);
            q.BlockAdvance = true;
            q.HandIn(p, CreateNpc("Troya"), Skin());
            Assert.That(q.Step, Is.EqualTo(2));
            Assert.That(((Inventory)(object)p.Inventory).RemovalCount, Is.Zero);
            Assert.That(((Packets)(object)p.Packets).Messages, Is.Empty);
        }
        [Test]
        public void TalkingBeforeGivingTheSkinDoesNotThankThePlayerOrMentionTheTibia()
        {
            var p = CreatePlayer();
            var q = TroyaQuest(p);
            q.Interact(p, CreateNpc("Troya"));
            Assert.That(q.Step, Is.EqualTo(2));
            Assert.That(((Packets)(object)p.Packets).Messages, Is.Empty);
        }
        [Test]
        public void ItemStartedOffersStillReceiveItemsWhenNoQuestIsActive()
        {
            var p = CreatePlayer();
            var offer = new Quest(new DbDataQuest { ID = 20060, Name = "Troya's Research" });
            offer.Receive(p, CreateNpc("Troya"), Skin());
            Assert.That(offer.OfferedHandIns, Is.EqualTo(1));
        }
        [Test]
        public void ATemporaryQuestFightDoesNotReceiveDialogueProtection()
        {
            var p = CreatePlayer();
            var npc = CreateNpc("Ciar");
            npc.AddOffer(new Quest(new DbDataQuest { ID = 20050, Name = "Dying Favor", StartName = "Ciar", MinLevel = 7, MaxLevel = 50 }));
            var names = ((Lazy<HashSet<string>>)typeof(QuestTalkTruce)
                .GetField("_questNpcNames", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)).Value;
            bool added = names.Add("Ciar");
            try
            {
                Assert.That(QuestTalkTruce.Holds(npc, p), Is.True, "The ordinary quest giver stays protected.");
                npc.TempProperties.SetProperty(ClassicQuests.EventSpawnProperty, "quest-player");
                Assert.That(QuestTalkTruce.Holds(npc, p), Is.False, "The hostile encounter must be allowed to fight.");
            }
            finally
            {
                if (added) names.Remove("Ciar");
            }
        }
    }
}
