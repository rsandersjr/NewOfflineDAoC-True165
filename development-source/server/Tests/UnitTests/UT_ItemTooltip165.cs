using System.Collections.Generic;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    /// <summary>Owner 2026-11-05: no database notes in tooltips; "Usable by" lists only classes that exist in 1.65.</summary>
    [TestFixture]
    public sealed class UT_ItemTooltip165
    {
        [TestCase("Non Public", false)]
        [TestCase("non public", false)]
        [TestCase("(NULL)", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("Atlas ROG | Armsman", false)]
        [TestCase("Classic level 50 class template", false)]
        [TestCase("Classic level 50 Sluaghbinder class template", false)]
        [TestCase("Loot gem (period Prima Atlas 2002); value as onyx", false)]
        [TestCase("Classic quest reward (Strange Beings)", true)]
        [TestCase("Give this stone to any trainer to receive a full respec.", true)]
        public void DatabaseNotesAreHidden(string description, bool shown) =>
            Assert.That(ItemTooltip165.ShowsDescription(description), Is.EqualTo(shown));

        [TestCase(eObjectType.PolearmWeapon, eInventorySlot.TwoHandWeapon, 0, 1, "Polearm Weapon", "Two-Handed")]
        [TestCase(eObjectType.TwoHandedWeapon, eInventorySlot.TwoHandWeapon, 2, 1, "2-Handed Weapon", "Two-Handed")]
        [TestCase(eObjectType.ThrustWeapon, eInventorySlot.LeftHandWeapon, 0, 2, "Thrusting Weapon", "Usable in Left Hand")]
        [TestCase(eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon, 2, 0, "Slashing Weapon", null)]
        public void WeaponDetailsMatchTheServerEquipSlot(eObjectType type, eInventorySlot slot, int storedHand,
            int handFlag, string family, string hand)
        {
            var lines = new List<string>();
            ItemTooltip165.AddWeaponDetails(lines, (int)type, (int)slot);
            Assert.That(lines, Is.EqualTo(hand == null ? new[] { family } : new[] { family, hand }));
            Assert.That(ItemTooltip165.WeaponHandFlag((int)type, (int)slot, storedHand), Is.EqualTo(handFlag));
        }

        [Test]
        public void ShieldDoesNotAcquireWeaponLabelsOrFlags()
        {
            var lines = new List<string>();
            ItemTooltip165.AddWeaponDetails(lines, (int)eObjectType.Shield, (int)eInventorySlot.LeftHandWeapon);
            Assert.That(lines, Is.Empty);
            Assert.That(ItemTooltip165.WeaponHandFlag((int)eObjectType.Shield, (int)eInventorySlot.LeftHandWeapon, 0), Is.Zero);
        }

        [Test]
        public void DisabledClassesAreLeftOutOfUsableBy()
        {
            var disabled = new HashSet<int> { 33, 34, 39, 58, 59, 60, 61, 62 };
            // Hammer of the Otherworld: Heretic (33) and Reaver (19)
            Assert.That(ItemTooltip165.UsableClasses([33, 19], disabled), Is.EqualTo(new List<int> { 19 }));
            Assert.That(ItemTooltip165.UsableClasses([19, 2], disabled), Is.EqualTo(new List<int> { 19, 2 }));
            // only disabled classes: kept as is, so it never looks usable by everyone
            Assert.That(ItemTooltip165.UsableClasses([33, 34], disabled), Is.EqualTo(new List<int> { 33, 34 }));
            // outside True 1.65 Mode nothing is filtered
            Assert.That(ItemTooltip165.UsableClasses([33, 19], (ISet<int>)null), Is.EqualTo(new List<int> { 33, 19 }));
        }
    }
}
