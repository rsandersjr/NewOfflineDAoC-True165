using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_ParallelCasterPetSpells
    {
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass TestClass;
            public override ICharacterClass CharacterClass => TestClass;
            public override byte Level => 50;
        }
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;
        private PetTestLanguageScope _language;
        [SetUp]
        public void Setup()
        {
            _language = new PetTestLanguageScope();
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown]
        public void Teardown()
        {
            GameServer.LoadTestDouble(_previousServer);
            _language.Dispose();
        }

        [TestCase(eCharacterClass.Cabalist, false)]
        [TestCase(eCharacterClass.Cabalist, true)]
        [TestCase(eCharacterClass.Enchanter, false)]
        [TestCase(eCharacterClass.Enchanter, true)]
        [TestCase(eCharacterClass.Spiritmaster, false)]
        [TestCase(eCharacterClass.Spiritmaster, true)]
        public void EveryFocusShieldRankUsesTheManagedCastPath(eCharacterClass characterClass, bool companion)
        {
            Bot bot = Create(characterClass);
            typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, companion);
            Spell damage = new(new DbSpell { Type = "DirectDamage", Target = "Enemy", CastTime = 2.5, Damage = 20, Range = 1500 }, 1);
            var known = new List<Spell> { damage };
            // Reject by spell type, not names or today's database IDs.
            for (int rank = 1; rank <= 50; rank++)
            {
                Spell shield = Shield(rank, true);
                known.Add(shield);
                Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(shield, true, bot), Is.False);
                Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(shield, false, bot), Is.False);
                Assert.That(bot.CastSpell(shield, null), Is.False, "Final cast guard must not start a channel or spend power");
            }
            bot.Spells = known;
            bot.SortSpells();
            Assert.That(bot.MiscSpells, Is.Null.Or.Empty);
            Assert.That(bot.HarmfulSpells, Does.Contain(damage));
        }

        [TestCase(eCharacterClass.Enchanter)]
        [TestCase(eCharacterClass.Spiritmaster)]
        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Animist)]
        public void TimedRealmShieldStaysAvailableToBuffAValewalker(eCharacterClass characterClass)
        {
            Bot bot = Create(characterClass);
            Spell timed = new(new DbSpell { SpellID = 4312, Type = "DamageShield", Target = "Realm", Duration = 1200, CastTime = 3 }, 4);
            bot.Spells = [timed];
            bot.SortSpells();
            Assert.That(AutonomousPetSupport.RequiresManagedPetFocusShield(bot, timed), Is.False);
            Assert.That(bot.MiscSpells, Does.Contain(timed));
            Assert.That(DOL.AI.Brain.BotBrain.IsMaintainableClassBuff(timed, true), Is.True);
            Assert.That(DOL.AI.Brain.BotBrain.BuffHelps(new ClassValewalker(), timed, true), Is.True);
            Assert.That(BotPetFocusShieldPolicy.KnowsPetFocusShield(bot), Is.False);
        }

        [TestCase(eCharacterClass.Enchanter)]
        [TestCase(eCharacterClass.Spiritmaster)]
        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Animist)] // catalog-based eligibility if a class learns a genuine focus shield
        public void HiredFocusShieldRequiresAnExplicitOptIn(eCharacterClass characterClass)
        {
            Bot bot = Create(characterClass);
            bot.DatabaseID = long.MaxValue - 21;
            bot.Spells = [Shield(51, true)];
            Assert.That(BotPetFocusShieldPolicy.KnowsPetFocusShield(bot), Is.False);
            bot.Spells = [Shield(1, true)];
            Assert.That(BotPetFocusShieldPolicy.KnowsPetFocusShield(bot), Is.True);
            Assert.That(CompanionOptions165.PetFocusShieldEnabled(bot), Is.True, "world bot keeps the existing combat policy");
            (string key, string[] choices) = CompanionOptions165.Option(CompanionOptions165.KindPetFocusShield).Value;
            Assert.That(choices[0], Is.EqualTo("Off"));
            var hired = (ConcurrentDictionary<long, OfflineHiredCompanionRecord>) typeof(HiredCompanions165)
                .GetField("Hired", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var record = new OfflineHiredCompanionRecord { BotId = bot.DatabaseID };
            Assert.That(hired.TryAdd(bot.DatabaseID, record), Is.True);
            try
            {
                Assert.That(CompanionOptions165.PetFocusShieldEnabled(bot), Is.False);
                record.Options = key + "=1";
                Assert.That(CompanionOptions165.PetFocusShieldEnabled(bot), Is.True);
                record.Options = "";
                Assert.That(CompanionOptions165.PetFocusShieldEnabled(bot), Is.False);
            }
            finally
            {
                hired.TryRemove(bot.DatabaseID, out _);
            }
        }

        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Enchanter)]
        public void OrdinaryPetBuffsStillWork(eCharacterClass characterClass)
        {
            Spell stats = new(new DbSpell { Type = "StrengthBuff", Target = "Pet", Duration = 1200, Value = 10 }, 1);
            Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(stats, false, Create(characterClass)), Is.True);
        }

        [Test]
        public void ActualPlayerHotbarPolicyIsUnchanged()
        {
            GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
            Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(Shield(1, true), true, player), Is.True);
        }

        private static Spell Shield(int rank, bool focus) => new(new DbSpell
        {
            SpellID = 900000 + rank, Type = "DamageShield", Target = "Pet", Duration = 8,
            IsFocus = focus, Pulse = focus ? 1 : 0, Damage = 5, CastTime = 2.5
        }, rank);
        private static Bot Create(eCharacterClass characterClass)
        {
            Bot bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            bot.TestClass = characterClass switch
            {
                eCharacterClass.Cabalist => new ClassCabalist(),
                eCharacterClass.Enchanter => new ClassEnchanter(),
                eCharacterClass.Spiritmaster => new ClassSpiritmaster(),
                eCharacterClass.Animist => new ClassAnimist(),
                _ => new ClassBonedancer()
            };
            return bot;
        }
    }
}
