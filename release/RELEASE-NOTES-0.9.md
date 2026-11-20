# Offline DAoC True 1.65 v0.9

Download **Download-OfflineDAoC-True165-v0.9.zip**, extract it, and run
**GET-OFFLINE-DAOC-TRUE165.cmd**. Choose **Update my game** for an existing install or
**New install** for a fresh world. The launcher applies content updates on the first START SERVER.

## What changed

- Hall of the Corrupt and Marfach Caverns load under their live server zone IDs. Characters saved inside
  can log in in place. The original Summoner's Hall is now built from the 1.64 files with locally upscaled
  textures, along with its portal connections to all three frontier dungeons.
- Guild rank Dues and Withdraw edits now save to their own fields.
- The Academy epic advances with the correct NPC. Required epic prerequisites and the Red Dagger starters
  can be picked up through level 50. Period drops were restored in Tepok's Mine.
- Weapon tooltips show weapon family and left-hand usability. Three weapons with broken durability and
  condition caps now wear normally, including existing affected copies. Several invalid spell references
  were corrected or cleared.
- Right-click cancels a held inventory item. Melee companions assist a stunned tank when appropriate, and
  fallen companions can remain available for resurrection for at least five minutes when the party can revive them.
- Hired companions finish their /grind rest and receive their full eligible group PvE XP share while catching up.
- Both portable reference tools are included under **tools**. Open the database's **index.html** in a browser.
  The crafting guide includes its Windows executable and Python/Tk source.

## Upgrading

The in-place update keeps the world database and saved progress. When an existing installation has edited
files that block the in-place update, the downloader can make a separate v0.9 install and transfer progress.
It leaves the old game in place. Compatibility hashes cover v0.1 through v0.8, including v0.4.1.
The full install, in-place update, and progress transfer were tested using v0.8.

See the [v0.9 changelog](https://github.com/zomboden/OfflineDAoC-True165/blob/v0.9/CHANGELOG.md#09)
for the full list.
