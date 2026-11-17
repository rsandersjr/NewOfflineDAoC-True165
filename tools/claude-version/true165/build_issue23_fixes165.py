"""Issue #2/#3 content corrections. Read source DB and original 1.64 archive only.

Run with --server <playable root> --client-164 <zip> --write. The launcher applies
the generated JSON at its next stopped-server refresh; this script never edits a DB.
"""
import argparse
import collections
import csv
import io
import json
from pathlib import Path
import sqlite3
import struct
import zipfile
import zlib

TAG = 'Issue 2/3 confirmed fixes 2026-11-17: '


def period_objects(path):
    with zipfile.ZipFile(path) as z:
        raw = z.read('gamedata.mpk')
    _, ds, ns, count = struct.unpack('<4I', bytes(b ^ i for i, b in enumerate(raw[5:21])))
    directory = zlib.decompress(raw[21 + ns:21 + ns + ds])
    start = 21 + ns + ds
    for i in range(count):
        record = directory[i * 284:(i + 1) * 284]
        if record[:256].split(b'\0')[0].lower() == b'objects.csv':
            fields = struct.unpack('<7I', record[256:])
            text = zlib.decompress(raw[start + fields[4]:start + fields[4] + fields[5]]).decode('latin1')
            return {int(r[0]): r for r in csv.reader(io.StringIO(text)) if r and r[0].isdigit()}
    raise ValueError('Original objects.csv missing')


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def changes(db, objects):
    rows = db.execute('SELECT DISTINCT i.Id_nb,i.Name,i.Realm,i.Object_Type,i.Item_Type,i.Model '
                      'FROM CraftedItem c JOIN ItemTemplate i ON i.Id_nb=c.Id_nb '
                      'WHERE i.Object_Type BETWEEN 32 AND 38').fetchall()
    prefixes = ['fine alloy', 'bronze', 'iron', 'steel', 'alloy', 'mithril', 'adamantium', 'asterite', 'netherium', 'arcanium',
                'woolen', 'linen', 'brocade', 'silk', 'gossamer', 'sylvan', 'seamist', 'nightshade', 'wyvernskin', 'silksteel',
                'rawhide', 'tanned', 'cured', 'hard', 'rigid', 'embossed', 'imbued', 'runed', 'eldritch', 'tempered']

    def family(r):
        name = r[1].lower()
        for prefix in prefixes:
            if name.startswith(prefix + ' '):
                name = name[len(prefix) + 1:]
                break
        return r[2], r[3], r[4], name

    votes = collections.defaultdict(collections.Counter)
    for r in rows:
        if r[5] in objects:
            votes[family(r)][r[5]] += 1
    updates = []
    for r in rows:
        wrong_mid_helm = r[2:5] == (2, 35, 21) and r[5] == 1206
        if r[5] in objects and not wrong_mid_helm:
            continue
        if not votes[family(r)]:
            raise ValueError('No verified same-family 1.64 replacement for ' + r[0])
        target = votes[family(r)].most_common(1)[0][0]
        if target not in objects:
            raise AssertionError(target)
        for table in ['ItemTemplate', 'ItemUnique']:
            # Existing crafted copies retain all stats, dyes, condition and ownership.
            where = ('Id_nb=' + quote(r[0]) if table == 'ItemTemplate' else
                     'Name=' + quote(r[1]) + f' AND Realm={r[2]} AND Object_Type={r[3]} AND Item_Type={r[4]}')
            updates.append({'sql': f'UPDATE {table} SET Model={target} WHERE {where} AND Model={r[5]}',
                            'reason': TAG + r[0] + f' model {r[5]} -> {target}: original 1.64 {objects[target][1]}'})
    claws = db.execute('SELECT DISTINCT i.Id_nb,i.Name,i.Realm FROM CraftedItem c JOIN ItemTemplate i ON i.Id_nb=c.Id_nb '
                       'WHERE i.Object_Type=25 AND i.Item_Type=11 AND i.Hand=1').fetchall()
    for idn, name, realm in claws:
        for table in ['ItemTemplate', 'ItemUnique']:
            where = 'Id_nb=' + quote(idn) if table == 'ItemTemplate' else 'Name=' + quote(name) + f' AND Realm={realm}'
            updates.append({'sql': f'UPDATE {table} SET Hand=2 WHERE {where} AND Object_Type=25 AND Item_Type=11 AND Hand=1',
                            'reason': TAG + idn + ': crafted hand-to-hand weapon usable in either hand, not two-handed'})
    return updates


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--server', type=Path, required=True)
    p.add_argument('--client-164', type=Path, required=True)
    p.add_argument('--write', action='store_true')
    p.add_argument('--output-root', type=Path, help='Stage JSON outside the playable installation')
    args = p.parse_args()
    db = sqlite3.connect((args.server / 'runtime/data/opendaoc-165.sqlite3.db').as_uri() + '?mode=ro', uri=True)
    updates = changes(db, period_objects(args.client_164))
    merchant_rows = []
    for realm in ['Alb', 'Mid', 'Hib']:
        listid = 'OFMerchant_' + realm
        used = {r[0] * 30 + r[1] for r in db.execute('SELECT PageNumber,SlotPosition FROM MerchantItem WHERE ItemListID=?', (listid,))}
        for idn in ['personal_house_necklace', 'guild_house_necklace', 'housing_entrance_necklace', 'hearth_bind_necklace']:
            if not db.execute('SELECT 1 FROM ItemTemplate WHERE Id_nb=?', (idn,)).fetchone():
                raise ValueError('Missing medallion: ' + idn)
            if db.execute('SELECT 1 FROM MerchantItem WHERE ItemListID=? AND ItemTemplateID=?', (listid, idn)).fetchone():
                continue
            slot = next(i for i in range(150) if i not in used)
            used.add(slot)
            merchant_rows.append({'ItemListID': listid, 'ItemTemplateID': idn, 'PageNumber': slot // 30,
                                  'SlotPosition': slot % 30, 'MerchantItem_ID': 'issue23-' + realm + '-' + idn})
    print(f'{len(updates)} guarded updates; {len(merchant_rows)} missing medallion listings')
    if args.write:
        output = args.output_root or args.server
        for sub in ['tools/claude-version/true165', 'runtime/true165']:
            folder = output / sub
            folder.mkdir(parents=True, exist_ok=True)
            path = folder / 'true165-rules.json'
            rules = json.loads((args.server / sub / 'true165-rules.json').read_text(encoding='utf-8'))
            existing = {u['sql']: u for u in rules['updates']}
            existing.update({u['sql']: u for u in updates})
            rules['updates'] = list(existing.values())
            path.write_text(json.dumps(rules, ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
            dest = folder / 'add-housingmedallions165.json'
            if merchant_rows or not dest.exists():
                dest.write_text(json.dumps({'table': 'MerchantItem', 'key': 'MerchantItem_ID',
                    'reason': TAG + 'restore access to the existing housing ceremony destinations',
                    'rows': merchant_rows}, indent=1) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
