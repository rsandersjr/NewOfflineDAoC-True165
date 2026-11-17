using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_PlayerQueuedSpellTarget
    {
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;

        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public override bool SpellQueue { get; set; }
            public override GameObject TargetObject { get; set; }
            public override bool IsIncapacitated => false;
            public override IPacketLib Out => new BotDummyPacketLib();
        }

        private sealed class Casting : CastingComponent
        {
            public Casting(GameLiving owner) : base(owner) { }
            protected override bool CanCastSpell() => true;
            public void SetCurrent(SpellHandler handler) => SpellHandler = handler;
        }

        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly SpellLine Line = new("queue-test", "queue-test", "queue-test", true);
        private ConcurrentDictionary<eSpellType, Func<GameLiving, Spell, SpellLine, ISpellHandler>> _factories;
        private Func<GameLiving, Spell, SpellLine, ISpellHandler> _previousFactory;
        private Player _player;
        private Casting _casting;
        private Queue<CastingComponent.StartSkillRequest> _requests;

        [SetUp]
        public void Setup()
        {
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            _player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            _player.SpellQueue = true;
            _player.ObjectState = GameObject.eObjectState.Active;
            _casting = new Casting(_player);
            _player.castingComponent = _casting;
            _requests = (Queue<CastingComponent.StartSkillRequest>)typeof(CastingComponent)
                .GetField("_startSkillRequests", Hidden).GetValue(_casting);
            _factories = (ConcurrentDictionary<eSpellType, Func<GameLiving, Spell, SpellLine, ISpellHandler>>)typeof(ScriptMgr)
                .GetField("_spellHandlerConstructorCache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _factories.TryGetValue(eSpellType.DamageAdd, out _previousFactory);
            _factories[eSpellType.DamageAdd] = (caster, spell, line) => new SpellHandler(caster, spell, line);
            _casting.SetCurrent(new SpellHandler(_player, Buff("Realm"), Line));
        }

        [TearDown]
        public void Teardown()
        {
            if (_casting != null)
                ServiceObjectStore.Remove<CastingComponent>(_casting);
            GameServer.LoadTestDouble(_previousServer);
            if (_previousFactory != null)
                _factories[eSpellType.DamageAdd] = _previousFactory;
            else
                _factories.TryRemove(eSpellType.DamageAdd, out _);
        }

        [TestCase("Realm", false)]
        [TestCase("Realm", true)]
        [TestCase("Enemy", false)]
        [TestCase("Enemy", true)]
        public void QueuedSpellKeepsRequestedTargetThroughSelectionChangeAndPromotion(string targetType, bool selectAnother)
        {
            _player.TargetObject = _player;
            Spell followUp = Buff(targetType);
            Assert.That(_player.CastSpell(followUp, Line, checkLos: false), Is.True);
            // Selection changes before even the asynchronous request is processed.
            _player.TargetObject = selectAnother ? (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC)) : null;
            ProcessNextRequest();
            SpellHandler queued = _casting.QueuedSpellHandler;
            Assert.That(queued, Is.Not.Null);
            Assert.That(queued.Target, Is.SameAs(_player));
            Assert.That(queued.Spell, Is.SameAs(followUp));
            _casting.PromoteQueuedSpellHandler();
            Assert.That(_casting.SpellHandler, Is.SameAs(queued));
            Assert.That(_casting.SpellHandler.Target, Is.SameAs(_player));
        }

        [Test]
        public void ReplacingFollowUpUsesTheNewRequestsTarget()
        {
            _player.TargetObject = _player;
            _player.CastSpell(Buff("Realm"), Line, checkLos: false);
            ProcessNextRequest();
            var other = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            _player.TargetObject = other;
            _player.CastSpell(Buff("Realm"), Line, checkLos: false);
            _player.TargetObject = null;
            ProcessNextRequest();
            Assert.That(_casting.QueuedSpellHandler.Target, Is.SameAs(other));
        }

        [Test]
        public void NoQueueKeepsCastTimeTargetingForShortInputBuffer()
        {
            _player.SpellQueue = false;
            _player.TargetObject = _player;
            _player.CastSpell(Buff("Realm"), Line, checkLos: false);
            Assert.That(((CastingComponent.CastSpellRequest)_requests.Peek()).Target, Is.Null);
        }

        private void ProcessNextRequest()
        {
            CastingComponent.StartSkillRequest request = _requests.Dequeue();
            request.StartSkill();
            request.ResetAndReturn();
        }

        private static Spell Buff(string target) => new(new DbSpell
        {
            SpellID = 990011, Name = "Queued test buff", Type = "DamageAdd", Target = target,
            Range = 1500, CastTime = 2.5, Duration = 600, Value = 5
        }, 1);
    }
}
