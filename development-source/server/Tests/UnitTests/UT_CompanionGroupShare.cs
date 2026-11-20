using DOL.GS;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.Tests.Unit
{
    /// <summary>Owner 2026-11-13: a level 5 hired companion beside three level 18s got almost nothing from a yellow level 18
    /// bear and four levels from an orange level 19 one. A companion below its player's level now always gets the full
    /// group share; everyone else keeps the period rule.</summary>
    [TestFixture]
    public class UT_CompanionGroupShare
    {
        [Test]
        public void ACatchingUpCompanionGetsTheFullShareOfAYellowKill()
        {
            // four members (threshold orange), the bear yellow to the level 18s and purple to the companion
            Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.YELLOW, ConColor.ORANGE, false, ConColor.PURPLE, true),
                Is.True);
        }

        [Test]
        public void EveryoneElseKeepsThePeriodRule()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.YELLOW, ConColor.ORANGE, false, ConColor.PURPLE, false),
                    Is.False, "a low gamebot (or a companion at its player's level) beside a yellow kill: reduced share");
                Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.ORANGE, ConColor.ORANGE, false, ConColor.PURPLE, false),
                    Is.True, "a challenge for the group: full share");
                Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.BLUE, ConColor.ORANGE, true, ConColor.BLUE, false),
                    Is.True, "the highest member itself");
                Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.YELLOW, ConColor.YELLOW, false, ConColor.YELLOW, false),
                    Is.True, "the monster cons no higher to the member than to the highest");
                Assert.That(AbstractServerRules.GroupShareIsFull(ConColor.GREEN, ConColor.YELLOW, false, ConColor.ORANGE, false),
                    Is.False);
            });
        }
    }
}
