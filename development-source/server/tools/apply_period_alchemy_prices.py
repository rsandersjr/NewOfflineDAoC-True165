"""Install the True 1.65 alchemy vendor-price corrections on a stopped SQLite world.

The CSV matches original 1.64 tdl.irf recipe IDs to the NPC sellback amounts in
Prissy's Alchemy Recipe Reference (pages 1–12):
https://www.tradeskills.de/daoc/daten/werkzeuge/Alchemy_Recipes.pdf

For single-output recipes, the template price is twice the documented buyback
at this world's 50% merchant ratio. Multi-output poison stacks are excluded.
The two material rows correct merchant aliases of St. John's wort. The 2.50g
Strong Elixir of Power uses two 25s distills and one 2g fermented wort. Light
Turquoise dyes use a 38s 40c wort, a 27s 35c flask and 3s of other materials.
"""

import argparse
import csv
import json
from pathlib import Path
import re
import sqlite3

PRICE_RULE_REASON = "Period alchemy vendor prices (1.64 recipes and Prissy's period reference)"


def price_rows():
    with Path(__file__).with_name("period_alchemy_prices.csv").open(
        encoding="utf-8", newline=""
    ) as csv_file:
        return list(csv.DictReader(csv_file))


def write_rules(path):
    """Update the launcher's final price rule without reformatting other content."""
    def sql_literal(value):
        return "'" + value.replace("'", "''") + "'"

    rows = price_rows()
    cases = " ".join(
        f"WHEN {sql_literal(row['item_id'])} THEN "
        f"CASE WHEN Price = {int(row['old_price'])} THEN {int(row['period_price'])} ELSE Price END"
        for row in rows
    )
    item_ids = ", ".join(sql_literal(row["item_id"]) for row in rows)
    sql = f"UPDATE ItemTemplate SET Price = CASE Id_nb {cases} END WHERE PackSize = 1 AND Id_nb IN ({item_ids})"
    rule = {"sql": sql, "reason": PRICE_RULE_REASON}
    data = path.read_bytes()
    document = json.loads(data)
    rules = document["updates"]
    matches = [entry for entry in rules if entry["reason"] == PRICE_RULE_REASON]
    if matches == [rule]:
        return
    if matches:
        raise ValueError("Remove the previous generated alchemy rule before regenerating")
    footer = re.search(rb"(\r?\n)[ \t]*\]\s*}\s*\Z", data)
    if not rules or list(document)[-1] != "updates" or footer is None:
        raise ValueError(f"Expected updates to be the final nonempty array in {path}")
    newline = footer.group(1)
    entry = newline.join(("  " + line).encode("utf-8") for line in json.dumps(rule, indent=1).splitlines())
    output = data[:footer.start()] + b"," + newline + entry + data[footer.start():]
    if json.loads(output)["updates"] != [*rules, rule]:
        raise ValueError("Generated price rule failed verification")
    path.write_bytes(output)


def read_prices(connection):
    ratio = connection.execute(
        "SELECT Value FROM ServerProperty WHERE Key = 'item_sell_ratio'"
    ).fetchone()
    if ratio != ("50",):
        raise ValueError("Expected the True 1.65 merchant sell ratio of 50%")

    changes = []
    for row in price_rows():
        item_id = row["item_id"]
        recipe_id = row["recipe_id"]
        original, corrected = map(int, (row["old_price"], row["period_price"]))
        item = connection.execute(
            "SELECT Price, PackSize FROM ItemTemplate WHERE Id_nb = ?", (item_id,)
        ).fetchone()
        if item is None or item[1] != 1 or item[0] not in (original, corrected):
            raise ValueError(f"Unexpected template price or pack size for {item_id}: {item}")
        if recipe_id and not connection.execute(
            "SELECT 1 FROM CraftedItem WHERE CraftedItemID = ? AND Id_nb = ? "
            "AND CraftingSkillType = 4", (recipe_id, item_id)
        ).fetchone():
            raise ValueError(f"Alchemy recipe {recipe_id} no longer makes {item_id}")
        if item[0] != corrected:
            changes.append((corrected, item_id, original))
    return changes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--backup", type=Path, help="New SQLite backup path (required with --apply)")
    parser.add_argument("--write-rules", action="store_true", help="Generate the launcher rule in the supplied JSON file")
    args = parser.parse_args()
    if args.apply != bool(args.backup):
        parser.error("--apply and --backup must be used together")
    if args.write_rules:
        if args.apply:
            parser.error("--write-rules and --apply are mutually exclusive")
        write_rules(args.database)
        return

    database_uri = args.database.resolve(strict=True).as_uri()
    mode = "rw" if args.apply else "ro"
    with sqlite3.connect(f"{database_uri}?mode={mode}", uri=True) as connection:
        changes = read_prices(connection)
        if not args.apply or not changes:
            print(f"{len(changes)} price changes pending")
            return

        with args.backup.open("xb"):
            pass
        with sqlite3.connect(args.backup) as backup:
            connection.backup(backup)
            if backup.execute("PRAGMA quick_check").fetchone() != ("ok",):
                raise RuntimeError("Backup failed SQLite integrity check")

        connection.execute("BEGIN IMMEDIATE")
        if read_prices(connection) != changes:
            raise RuntimeError("Prices changed during backup")
        cursor = connection.executemany(
            "UPDATE ItemTemplate SET Price = ? WHERE Id_nb = ? AND Price = ?", changes
        )
        if cursor.rowcount != len(changes) or read_prices(connection):
            raise RuntimeError("Price verification failed")
        print(f"Updated {len(changes)} template prices. Backup: {args.backup}")


if __name__ == "__main__":
    main()
