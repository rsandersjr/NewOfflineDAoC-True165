using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DOL.Database;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_BotSicknessLocking
    {
        private sealed class InertServer : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();
        public class EmptyReads : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (!method.Name.StartsWith("Select") && !method.Name.StartsWith("Find"))
                    throw new InvalidOperationException("Unexpected database write: " + method.Name);
                Type type = method.ReturnType;
                return type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)
                    ? Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0])) : null;
            }
        }
        private GameServer _oldServer;
        [SetUp]
        public void SetUp()
        {
            _oldServer = GameServer.Instance;
            GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        }
        [TearDown]
        public void TearDown() => GameServer.LoadTestDouble(_oldServer);

        private static readonly FieldInfo Effects = typeof(EffectListComponent).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo EffectsLock = typeof(EffectListComponent).GetField("_effectsLock", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Publish = typeof(EffectListComponent).GetMethod("PublishResurrectionSickness", BindingFlags.Instance | BindingFlags.NonPublic);

        private static GameBot Bot()
        {
            var bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
            bot.effectListComponent = EffectListComponent.Create(bot);
            return bot;
        }

        private static void SetSickness(GameBot bot, eEffect type, double? value)
        {
            var effects = (Dictionary<eEffect, List<ECSGameEffect>>)Effects.GetValue(bot.effectListComponent);
            if (value == null)
                effects.Remove(type);
            else
            {
                var effect = (ECSGameSpellEffect)RuntimeHelpers.GetUninitializedObject(typeof(ECSGameSpellEffect));
                effect.SpellHandler = new SpellHandler(bot, new Spell(new DbSpell { Value = value.Value, Type = "BaseArmorFactorBuff", Target = "Self" }, 1),
                    new SpellLine("Test", "Test", "Test", true));
                effects[type] = new List<ECSGameEffect> { effect };
            }
            Publish.Invoke(bot.effectListComponent, new object[] { type });
        }

        [Test]
        public void SicknessStartsStacksAndClearsWithoutLeavingStalePenalty()
        {
            var bot = Bot();
            SetSickness(bot, eEffect.ResurrectionIllness, 25);
            Assert.That(bot.effectListComponent.ResurrectionSicknessPenalty, Is.EqualTo(0.25));
            SetSickness(bot, eEffect.RvrResurrectionIllness, 50);
            Assert.That(bot.effectListComponent.ResurrectionSicknessPenalty, Is.EqualTo(0.75));
            SetSickness(bot, eEffect.ResurrectionIllness, null);
            Assert.That(bot.effectListComponent.ResurrectionSicknessPenalty, Is.EqualTo(0.50));
            SetSickness(bot, eEffect.RvrResurrectionIllness, null);
            Assert.That(bot.effectListComponent.ResurrectionSicknessPenalty, Is.Zero);
        }

        [Test]
        public void SimultaneousCrossActorEffectivenessReadsDoNotAcquireEitherEffectLock()
        {
            bool old = DOL.GS.ServerProperties.Properties.CLASSIC165_MODE;
            DOL.GS.ServerProperties.Properties.CLASSIC165_MODE = true;
            var a = Bot();
            var b = Bot();
            SetSickness(a, eEffect.ResurrectionIllness, 25);
            SetSickness(b, eEffect.ResurrectionIllness, 50);
            Task<double> readA = null, readB = null;
            try
            {
                // Concentration can read a caster while either actor's list is
                // locked. Old code blocks here; release in finally keeps a failed
                // regression test from leaving permanently deadlocked workers.
                lock ((Lock)EffectsLock.GetValue(a.effectListComponent))
                lock ((Lock)EffectsLock.GetValue(b.effectListComponent))
                {
                    readA = Task.Run(() => a.Effectiveness);
                    readB = Task.Run(() => b.Effectiveness);
                    Assert.That(Task.WaitAll(new Task[] { readA, readB }, TimeSpan.FromSeconds(3)), Is.True,
                        "Concentration sickness reads must never wait for another actor's effect lock");
                    Assert.That(readA.Result, Is.EqualTo(0.75));
                    Assert.That(readB.Result, Is.EqualTo(0.50));
                }
            }
            finally
            {
                if (readA != null && readB != null) Task.WaitAll(new Task[] { readA, readB }, TimeSpan.FromSeconds(3));
                DOL.GS.ServerProperties.Properties.CLASSIC165_MODE = old;
            }
        }
    }
}
