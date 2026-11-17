using System.Collections.Generic;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Dismisses only a real rider's pets; never a grouped bot or its pets.</summary>
    public static class PlayerStablePetCleanup
    {
        public static HashSet<GameNPC> Collect(GamePlayer player, IEnumerable<GameNPC> nearby)
        {
            var pets = TemporaryCompanionPetHealing.CollectOwnedPets(player, player?.ControlledBrain, nearby);
            pets.RemoveWhere(pet => !TemporaryCompanionPetHealing.IsDirectPlayerPet(pet, player));
            return pets;
        }

        public static void ReleaseForRide(GamePlayer player)
        {
            if (player == null) return;
            // Capture the complete attached tree before release removes ownership links.
            // Include independent nearby summons not attached to ControlledBrain.
            var pets = Collect(player, player.GetNPCsInRadius(WorldMgr.VISIBILITY_DISTANCE));
            foreach (GameNPC pet in pets)
            {
                if (pet.ObjectState != GameObject.eObjectState.Active) continue;
                pet.StopCurrentSpellcast();
                pet.StopAttack();
                pet.StopFollowing();
                if (pet.Brain is ControlledMobBrain brain)
                    brain.OnRelease();
                // Native release handles charm restoration, children, shade and summon counters.
                // Never call Die(): it awards kills/loot and harms Necromancer HP.
            }
        }
    }
}
