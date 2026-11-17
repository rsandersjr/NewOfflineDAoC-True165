# Confirmed issue fixes - 17 November 2026

Installed in the isolated Claude Version server and Godot client. The server remains stopped. Source is prepared
for 0.8; this does not replace the published 0.7 downloads. The private D: patches contain the built local changes.
No monster difficulty, combat values, BAF rules or global navigation meshes were changed.

## What changed and why

| Finding | Evidence and change |
|---|---|
| Buff/concentration deadlock | Two captures of the previous frozen run showed casting workers in `GetEffects -> GameBot.SicknessEffectiveness165 -> MaxConcentrationCalculator -> CheckConcentrationCost`, while applying effects under another actor's effect lock. The main loop waited for those workers. `EffectListComponent.cs:39,474,873,978` now publishes the current sickness penalty when the list changes; `GameBot.cs:5923` reads it without taking another actor's lock. The original sickness values, concentration rules and cleanup remain. The precise bots/lock owners were not captured. |
| Interior upgrades unavailable | `source/new-client/godot/game/net/DaocClient.cs:1403` had masked housing purchase types 66-71 with `0x3F`. Housing windows 4/11 now retain the full byte. Ordinary weapon/type parsing retains the mask. |
| Housing appearance and weather | The packets already carried the colors and purchase flags, but `house_view.gd` did not use several of them. It now applies awning/rug dyes, interior flags and guild patterns/logos. Original converted NIF fireplace particle specs are instantiated. Interior entry/exit controls the housing-only rain guard in `sky_system.gd`; fog and outdoor weather are unchanged. |
| Mansion ladder unusable | Converted `climb_` meshes were hidden without collision. `house_view.gd:436` now supplies a `ClimbBodies` collider, matching the existing player climb detector. No player movement constants changed. |
| Crafted claws rejected by companions | A `CraftedItem -> ItemTemplate` query found 40 hand-to-hand recipes with `Object_Type=25`, `Item_Type=11`, `Hand=1` (two-handed). Their slot requires either-hand weapons. Guarded content updates change only `Hand` to 2, including matching existing unique items. `AllowedClasses='0'` was not the cause: the template loader already normalizes it. |
| Claw orientation | The current meshes/grips differ from the original 1.64 models. For example, the later MoonFang HELD transform contains about a quarter turn, while the period model's HELD is identity. All 24 models 959-982 now use original 1.64 meshes and HELD/HELD2 frames, rather than a guessed global rotation. Original textures were converted and upscaled 4x through the existing ESRGAN pipeline. |
| Later armor appearance | A crafted-armor query compared models against the original 1.64 `objects.csv`: 21 template models were absent. One additional Midgard heavy starkakedja helm used model 1206, a Hibernian scale appearance. Replacements use verified period IDs from the same crafted family. There are 22 template corrections using 13 classic model IDs: 51, 53, 54, 80, 270, 271, 272, 274, 276, 277, 824, 831, 834. Models 824/831/834 have rebuilt original helm meshes/skins; the ten body appearances already have classic meshes and exact 4x texture copies. Guarded updates touch Model only. |
| Focus recipes confusing | The recipes exist, including ten Oozing Spell Stone / Creeping Path Focus ranks. The list lacked effect labels. `pipeline/build_recipes.py` adds focus name/value and grouping for all 230 specialization-focus recipes (Albion 110, Midgard 50, Hibernia 70). IDs, product names, ingredients, levels and actual bonuses are preserved. |
| Focus could be applied to non-staves | `SpellCrafting.BonusFitsItem` now enforces the period staff restriction for focus bonus types 120-144 in both ordinary combining and `CraftEnchant165` planning/catalog filtering. It also validates owned gems. Other bonus types retain their behavior. |
| Weapon hand information missing | The item summary and crafting detail show main hand, either hand or two-handed use from existing item data. This does not change equipment rules. |
| Housing medallions missing | Templates and `OFTeleporter` destinations existed, but `MerchantItem` had no matching stock. Added 12 rows at unused slots: four each for `OFMerchant_Alb` (Sall Fadri), `OFMerchant_Mid` (Gwulla), `OFMerchant_Hib` (Araisa). They sell personal-house, guild-house, housing-entrance and hearth-bind medallions. Existing prices and teleport handlers are unchanged. |

Server paths in the table are relative to `development-source/server/GameServer` unless stated otherwise.

## Deliberately left unchanged

- Companion guild colors turning gray on housing travel: not reproduced/proven by this investigation; the house
  emblem fix does not claim to fix companion equipment emblems.
- Green NPC and gray vault hook markers: these are the original 1.64 marker assets.
- Picking the vault/alchemy table, the enchanting dropdown and skill-points layout: no confirmed defect/period
  mismatch established. They were not changed.
- Soultorn single pulls, same-species-only BAF and smaller add limits: not established as period rules. No balance
  changes were made.
- Avernal Quasit flight/pathing: the movement path is suspicious, but a safe confirmed correction was not established.
  No speculative flying/navigation fix was installed.
- A universal free teleport network at capitals/SI entrances: not established as the standard 1.65 realm-server
  behavior. Only the demonstrated missing medallion stock was restored.

## Validation

- Final Release solution build: **0 errors**, 638 existing warnings.
- Full server test suite: **5,147 passed, 0 failed**. Optional/manual integration probes are not an in-game test.
- New deterministic lock test holds both actor effect locks while concurrent effectiveness reads complete; the
  former implementation blocks those reads. Sickness stacking/removal checks also pass.
- Focus tests exercise staff acceptance, non-staff rejection, owned gems and unchanged non-focus bonuses.
- Client C# build: **0 warnings, 0 errors**.
- All six changed/new GDScripts passed `load_check.gd`.
- Headless Godot: **48 hand attachments passed**, covering 24 original claw models in both hands; checks include
  loaded geometry and the original grip transforms. This is an attachment check, not an owner in-game visual test.
- Headless housing: purchase visibility, awning/four rug tints, ladder collision, fireplace particle creation and
  entry/exit weather state passed using the converted Midgard mansion. This does not simulate buying a real house
  over the network or a troll climbing it in-game.
- Staging SQLite copy: 124 guarded updates and 12 stock rows applied; a second update pass changed nothing. All
  ItemTemplate/ItemUnique fields except the two permitted columns (Model/Hand) had identical before/after hashes.
  62 templates changed (22 armor + 40 claws); there were no matching unique copies in this local world.
- Standard stopped-server launcher refresh applied the content locally. Query confirms zero remaining bad crafted
  H2H Hand flags and all 12 merchant rows. The heavy starkakedja helm now uses model 834.
- Installed recipe comparison confirms metadata/icon changes only: no IDs, names, ingredients or levels changed;
  230 focus labels, 2,003 weapon hand labels and 39 corrected recipe icons.
- GameServer.dll SHA256 `31540252041c2a9aa934b0ad42c18753757135d6950b11254f48e2204c74816e`.
  CoreDatabase.dll SHA256 `3c491a91ba924156908065e77fb6326ef997ae20d6028c55fc89d78520159414`.
  All three runtime locations match. Client C# was rebuilt too.

A live session is still needed to confirm long-run deadlock absence and the complete housing purchase/climb visuals.
Tests cannot guarantee zero regressions or prove that every possible server freeze is gone.

## Rebuilding generated client content

The repository does not contain the copyrighted 1.64 archive or converted models/textures. Use your own copy.
After the normal content builders, run these finalizers from `source/new-client` with the server/client stopped:

```powershell
python ../../tools/claude-version/true165/build_issue23_fixes165.py --server <playable-root> --client-164 <1.64.zip> --write
python pipeline/prepare_classic_crafted_assets.py --client <client-root> --client-164 <1.64.zip> --rules <playable-root>/runtime/true165/true165-rules.json --output <staging-content>
python pipeline/prepare_classic_claws.py --client <client-root> --client-164 <1.64.zip> --output <staging-content>
```

The asset tools write **only the named staging output**. Back up the corresponding installed files, then overlay
that content into `godot/content`. Pack the new helm and `weapons/classic164_n_hth_*` folders with
`godot/tools/pack_scenes.gd` (only those folders). Do not replace other weapon metadata or content with an old snapshot.
The ESRGAN executable and models expected by the existing pipeline must be present in the client's tools folder.

Apply content with the stopped-server `OfflineDAoC.exe --refresh-165` or the next START SERVER, then run
`python pipeline/build_recipes.py --annotate-existing`. This annotation option retains established installed recipe
spelling/list entries and refreshes only the new labels, handedness and icons. A clean build can omit the option.

Run `tools/build_csharp.ps1`, the server Release build/tests and:

```powershell
Godot --headless --path godot -s res://tests/load_check.gd -- res://game/housing/house_view.gd res://game/sky_system.gd res://game/ui/craft_window.gd res://game/ui/item_db.gd res://tests/classic_claw_alignment_check.gd res://tests/issue23_housing_check.gd
Godot --headless --path godot -s res://tests/classic_claw_alignment_check.gd
Godot --headless --path godot -s res://tests/issue23_housing_check.gd
```

## Local rollback

The local deployment backup is `deployment-backups/issue23-deadlock-20261117`. Restore its saved client scripts,
metadata/helm files and server DLLs with everything stopped. Restore both saved true165-rules.json copies and remove
`add-housingmedallions165.json` from both content directories before a refresh, or the launcher will reapply them.
The new private claw folders can remain unused after restoring weapons.json. For a complete pre-change world restore,
use `runtime/data/backups/before-issue23-and-deadlock-2026-11-17_012558.sqlite3.db`; this also rolls back any later play.
Prefer targeted rollback to replacing a played database. No saves, credentials or private logs are in GitHub.

Each feature has a separate commit. GitHub issues #2/#3 are resolved only for the confirmed portions above; the
remaining observations were explicitly excluded, rather than silently claimed fixed.
