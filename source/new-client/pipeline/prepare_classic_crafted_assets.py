"""Prepare original 1.64 helm meshes and 4x textures for the issue-2 armor fixes.

Reads the real archive, writes only --output (a staging folder). Existing assets
are not used as the source of the rebuilt helm meshes or their skin textures.
"""
import argparse
import csv
import io
import json
from pathlib import Path
import re
import struct
import zipfile
import zlib


def unpack(raw):
    _, ds, ns, count = struct.unpack('<4I', bytes(v ^ i for i, v in enumerate(raw[5:21])))
    directory = zlib.decompress(raw[21 + ns:21 + ns + ds])
    start = 21 + ns + ds
    out = {}
    for i in range(count):
        record = directory[i * 284:(i + 1) * 284]
        fields = struct.unpack('<7I', record[256:])
        name = record[:256].split(b'\0')[0].decode('latin1').lower()
        out[name] = zlib.decompress(raw[start + fields[4]:start + fields[4] + fields[5]])
    return out


def prepare(client, archive, rules, output):
    import nif_static
    import build_zone
    from build_zone import upscale_dir
    build_zone.ESRGAN = client / 'tools/realesrgan/realesrgan-ncnn-vulkan.exe'
    from PIL import Image
    targets = sorted({int(re.search(r'SET Model=(\d+)', u['sql'])[1]) for u in rules['updates']
                      if u.get('reason', '').startswith('Issue 2/3 confirmed fixes') and
                      u['sql'].startswith('UPDATE ItemTemplate SET Model=')})
    z = zipfile.ZipFile(archive)
    names = {n.lower(): n for n in z.namelist()}
    gd = unpack(z.read(names['gamedata.mpk']))
    table = lambda n: {int(r[0]): r for r in csv.reader(io.StringIO(gd[n].decode('latin1'))) if r and r[0].isdigit()}
    objects, skins, items = table('objects.csv'), table('pskins.csv'), table('items.csv')
    source_records = json.loads((client / 'godot/content/characters/classic_armor.json').read_text())
    reports, archives = [], {}
    src = output / 'helms_classic/textures/original'
    src.mkdir(parents=True, exist_ok=True)
    for model in targets:
        row = objects[model]
        record = source_records['models'][str(model)]
        # Body pieces already use the classic figure's original skin and mesh.
        if not int(row[12] or 0):
            for key in record.get('skins', {}).values():
                old = client / 'godot/content/characters/classic_textures/original' / (key + '.png')
                up = client / 'godot/content/characters/classic_textures/x4plus' / (key + '.png')
                if not old.exists() or not up.exists() or Image.open(up).size != tuple(v * 4 for v in Image.open(old).size):
                    raise ValueError('Missing exact 4x classic body texture: ' + key)
            reports.append({'model': model, 'period_name': row[1], 'body_textures_4x': True})
            continue
        skin = skins[int(row[12])]
        number = int(skin[4] or 0)
        base = Path(skin[2]).stem.lower()
        key = base + (f'__a{number}' if number else '')
        if record.get('skins', {}).get('helm') != key:
            raise ValueError('Existing classic record differs from original 1.64 skin: ' + str(model))
        arcname = f'items/pskins/pskin{number:03d}.mpk'
        if arcname not in names:
            arcname = next(n for n in names if n.endswith(f'/pskin{number:03d}.mpk'))
        if arcname not in archives:
            archives[arcname] = unpack(z.read(names[arcname]))
        texname = next(n for n in archives[arcname] if Path(n).stem.lower() == base)
        Image.open(io.BytesIO(archives[arcname][texname])).convert('RGBA').save(src / (key + '.png'))
        built = []
        for col in (2, 24, 28, 29, 30, 31):
            item_id = int(row[col] or 0) if col < len(row) else 0
            if not item_id:
                built.append('')
                continue
            name = items[item_id][2].lower()
            raw = z.read(names['items/' + name + '.nif'])
            folder = name + '__' + key
            dst = output / 'helms_classic' / folder / 'model.gltf'
            dst.parent.mkdir(parents=True, exist_ok=True)
            nif_static.convert(raw, str(dst), '../textures/x4plus')
            doc = json.loads(dst.read_text())
            images = doc.get('images', [])
            # These period basic helms have one skin texture; refuse unexpected
            # multi-texture geometry rather than silently recoloring it all.
            if len(images) != 1:
                raise ValueError('Unexpected texture layout: ' + name)
            images[0]['uri'] = '../textures/x4plus/' + key + '.png'
            dst.write_text(json.dumps(doc), encoding='utf-8')
            built.append(folder + '/model.gltf')
        source_records['models'][str(model)]['helm'] = built
        reports.append({'model': model, 'period_name': row[1], 'skin': key, 'helm_files': built})
    upscale_dir(src, output / 'helms_classic/textures/x4plus', factor_after=4)
    (output / 'characters').mkdir(exist_ok=True)
    (output / 'characters/classic_armor.json').write_text(json.dumps(source_records, indent=1), encoding='utf-8')
    (output / 'classic-crafted-assets-report.json').write_text(json.dumps(reports, indent=1), encoding='utf-8')
    return reports


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--client', type=Path, required=True)
    p.add_argument('--client-164', type=Path, required=True)
    p.add_argument('--rules', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    print(json.dumps(prepare(a.client, a.client_164, json.loads(a.rules.read_text(encoding='utf-8')), a.output), indent=1))
