"""Generate guarded True 1.65 color fixes for the dye templates in issue #22."""

import argparse
import json
from pathlib import Path

FIXES = (
    ("black_enamel2", 495, 0, 74),
    ("blue_enamel2", 495, 0, 52),
    ("rust_enamel2", 495, 0, 49),
    ("rust_enamel3", 495, 0, 49),
    ("green_cloth_dye2", 229, 59, 69),
)
TAG = "Issue #22 dye colors: "


def updates():
    values = ", ".join(f"('{item}', {model}, {old}, {new})" for item, model, old, new in FIXES)
    cte = f"WITH fixes(id, model, old_color, new_color) AS (VALUES {values}) "
    for table, match in (
        ("ItemTemplate", "Id_nb = fixes.id AND Model = fixes.model AND Item_Type = 40"),
        ("ItemUnique", "substr(Id_nb, 1, length(fixes.id) + 1) = fixes.id || '#' "
         "AND Model = fixes.model AND Item_Type = 40"),
        ("Inventory", "ITemplate_Id = fixes.id OR EXISTS (SELECT 1 FROM ItemUnique u "
         "WHERE u.Id_nb = Inventory.UTemplate_Id AND u.Model = fixes.model AND u.Item_Type = 40 "
         "AND substr(u.Id_nb, 1, length(fixes.id) + 1) = fixes.id || '#')"),
    ):
        condition = f"WHERE ({match}) AND {table}.Color = fixes.old_color"
        yield {
            "sql": f"{cte}UPDATE {table} SET Color = (SELECT new_color FROM fixes {condition}) "
                   f"WHERE EXISTS (SELECT 1 FROM fixes {condition})",
            "reason": TAG + table,
        }


def apply(path):
    original = path.read_bytes()
    rules = json.loads(original)
    expected = list(updates())
    present = [rule for rule in rules["updates"] if rule["reason"].startswith(TAG)]
    if present == expected:
        return
    if present:
        raise ValueError(f"Conflicting dye migration in {path}")
    # Preserve the surrounding curated file byte-for-byte.
    newline = b"\r\n" if b"\r\n" in original else b"\n"
    marker = b' "updates": [' + newline
    if original.count(marker) != 1:
        raise ValueError(f"Missing updates section in {path}")
    lines = [json.dumps(rule, indent=1).splitlines() for rule in expected]
    block = newline.join(b"  " + line.encode("utf-8") for group in lines
                         for line in [*group[:-1], group[-1] + ","])
    result = original.replace(marker, marker + block + newline, 1)
    assert json.loads(result)["updates"] == expected + rules["updates"]
    path.write_bytes(result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    root = parser.parse_args().root
    for folder in ("runtime/true165", "tools/claude-version/true165"):
        apply(root / folder / "true165-rules.json")
