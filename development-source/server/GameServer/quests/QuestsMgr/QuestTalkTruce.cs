using System;
using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS.Quests
{
    /// <summary>
    /// Some quests send the player to talk to, whisper, or hand items to a monster: Tiomoid the Siabra Gurite in Lough
    /// Gur (Lost Townfolk of Bran Llyr), Trefor and Curmie (Occupation of Lough Gur), Shiake (Mother's Children), the
    /// XP-item turn-in NPCs of the Shrouded Isles tribes. Their faction is kill-on-sight by default, or they have no
    /// faction and attack anyone, so the player was attacked on arrival or turned away with a dirty look and the quest
    /// could not go on (quest audit 2026-10-13: 67 such NPCs). While a player is on a step that talks to, whispers to
    /// or hands items to one of them, or can take one of its quests, that NPC does not aggro the player and talks to
    /// them. Everyone else meets the NPC as before, and a player who attacks it gets a fight.
    /// </summary>
    public static class QuestTalkTruce
    {
        private static readonly Lazy<HashSet<string>> _questNpcNames = new(LoadQuestNpcNames);

        /// <summary>Called from mob aggro checks: cheap for every NPC that is not a data quest's talk target.</summary>
        public static bool Holds(GameNPC npc, GamePlayer player)
        {
            // A temporary encounter is the fight, not the normal NPC that shares its name.
            // Ciar's Dying Favor offer otherwise also protects the Enchanting Willow attacker.
            if (npc?.TempProperties?.GetProperty<string>(ClassicQuests.EventSpawnProperty) is { Length: > 0 })
                return false;

            if (npc == null || player == null || npc.Realm != eRealm.None ||
                !_questNpcNames.Value.Contains(npc.Name) && !ClassicQuests.IsTalkTargetName(npc.Name))
                return false;

            // A hostile monster that only shares its name with a realm quest NPC of its region (QuestRealmImpostors).
            if (QuestRealmImpostors.IsMarked(npc))
                return false;

            foreach (var pair in player.QuestList) // not .Keys: that copies the keys on every aggro check
            {
                if (pair.Key is not DataQuest dataQuest)
                    continue;
                if (TalksTo(dataQuest.StepType) &&
                    QuestNames.Same(dataQuest.TargetName, npc.Name) && (dataQuest.TargetRegion == 0 || dataQuest.TargetRegion == npc.CurrentRegionID))
                    return true;
                // A choose or trade step is a talk step whatever its StepType (96 choose steps are SearchFinish): an
                // aggressive NPC attacked the player whispering for a reward (quest audit 2026-11-11, P07).
                if (ClassicQuests.IsTalkStep(dataQuest.ID, dataQuest.Step, npc.Name))
                    return true;
                if (TalkedToBeforeThisHunt(dataQuest, npc))
                    return true;
            }

            // A quest it gives. Level range only: the full qualification check reads the database.
            List<DataQuest> given = npc.DataQuestList;
            for (int i = 0; i < given.Count; i++)
            {
                DataQuest quest = i < given.Count ? given[i] : null;
                if (quest?.DBDataQuest == null || quest.StartType == DataQuest.eStartType.KillComplete)
                    continue;
                if (player.Level >= quest.DBDataQuest.MinLevel && player.Level <= quest.DBDataQuest.MaxLevel)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The NPC the player last talked to (or handed something to) on this quest, while the player now hunts something
        /// else: it waits instead of turning hostile the moment the talk step ends. Quest audit 2026-11-11 (P02): in Essence
        /// of Hostility the Fallen One attacked as soon as the bracer was delivered; in the period he attacks only after the
        /// hill hound is dead. The truce ends when a step targets the NPC itself (or the hunt is over).
        /// </summary>
        public static bool TalkedToBeforeThisHunt(DataQuest quest, GameNPC npc) =>
            quest.Step > 1 && TalkedToBeforeThisHunt(step => quest.TryGetStep(step, out var type, out string target, out ushort region)
                ? (type, target, region) : null, quest.Step, npc.Name, npc.CurrentRegionID);

        public static bool TalkedToBeforeThisHunt(Func<int, (DataQuest.eStepType Type, string Target, ushort Region)?> stepAt, int current,
            string npcName, ushort npcRegion)
        {
            if (stepAt(current) is not { } now || now.Type is not (DataQuest.eStepType.Kill or DataQuest.eStepType.KillFinish) ||
                QuestNames.Same(now.Target, npcName))
                return false;
            for (int step = current - 1, looked = 0; step >= 1 && looked < 6; step--, looked++)
            {
                if (stepAt(step) is not { } before)
                    return false;
                if (TalksTo(before.Type))
                    return QuestNames.Same(before.Target, npcName) && (before.Region == 0 || before.Region == npcRegion);
                if (before.Type is not (DataQuest.eStepType.Kill or DataQuest.eStepType.KillFinish or DataQuest.eStepType.Search or
                    DataQuest.eStepType.SearchFinish) || QuestNames.Same(before.Target, npcName))
                    return false;
            }
            return false;
        }

        public static bool TalksTo(DataQuest.eStepType step) => step is DataQuest.eStepType.Deliver or DataQuest.eStepType.DeliverFinish or
            DataQuest.eStepType.Interact or DataQuest.eStepType.InteractFinish or DataQuest.eStepType.Whisper or
            DataQuest.eStepType.WhisperFinish or DataQuest.eStepType.Collect or DataQuest.eStepType.CollectFinish;

        /// <summary>Start NPCs and talk/whisper/hand-in targets of every data quest (names compared without case, as DataQuest does).</summary>
        private static HashSet<string> LoadQuestNpcNames()
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

            foreach (DbDataQuest quest in GameServer.Database.SelectAllObjects<DbDataQuest>())
            {
                if (!string.IsNullOrEmpty(quest.StartName))
                    names.Add(quest.StartName);

                string[] types = (quest.StepType ?? string.Empty).Split('|');
                string[] targets = (quest.TargetName ?? string.Empty).Split('|');

                for (int i = 0; i < Math.Min(types.Length, targets.Length); i++)
                {
                    if (!byte.TryParse(types[i], out byte type) || !TalksTo((DataQuest.eStepType) type))
                        continue;

                    string name = targets[i].Split(';')[0];
                    if (name.Length > 0)
                        names.Add(name);
                }
            }

            return names;
        }
    }
}
