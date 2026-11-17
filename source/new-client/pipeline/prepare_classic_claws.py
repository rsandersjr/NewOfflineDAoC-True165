"""Stage 1.64 Savage claw meshes, original grips and private 4x textures."""
import argparse
import csv
import io
import json
from pathlib import Path
import zipfile
import numpy as np
from prepare_classic_crafted_assets import unpack


def grip(raw, name):
    from netimmerse import read_nif
    import nif_static
    found = []
    def walk(node, parent):
        if node is None or found:
            return
        matrix = nif_static.local_matrix(node) @ parent
        if str(node.get('name') or '').strip().upper() == name:
            found.append(matrix)
            return
        for child in node.get('children') or []:
            walk(child, matrix)
    for node in read_nif(raw).roots:
        walk(node, np.identity(4))
    if not found or np.allclose(found[0], np.identity(4), atol=1e-3):
        return None
    a = np.linalg.inv(found[0]).T
    return [round(float(v), 5) for v in (*a[:3, 0], *a[:3, 1], *a[:3, 2], *a[:3, 3])]


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--client', type=Path, required=True)
    p.add_argument('--client-164', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    import nif_static, build_zone
    from PIL import Image
    build_zone.ESRGAN = args.client / 'tools/realesrgan/realesrgan-ncnn-vulkan.exe'
    z = zipfile.ZipFile(args.client_164)
    names = {n.lower(): n for n in z.namelist()}
    g = unpack(z.read(names['gamedata.mpk']))
    rows = lambda key: {r[0]: r for r in csv.reader(io.StringIO(g[key].decode('latin1'))) if r and r[0].isdigit()}
    objects, items = rows('objects.csv'), rows('items.csv')
    data = json.loads((args.client / 'godot/content/weapons/weapons.json').read_text())
    src = args.output / 'weapons/classic164_textures/original'
    src.mkdir(parents=True, exist_ok=True)
    report = []
    for mid, row in objects.items():
        if len(row) <= 19 or row[19] != '13' or mid not in data['models']:
            continue
        name = items[row[2]][2].lower()
        if not name.startswith('n_hth_'):
            continue
        raw = z.read(names['items/' + name + '.nif'])
        folder = 'classic164_' + name
        dst = args.output / 'weapons' / folder / 'model.gltf'
        dst.parent.mkdir(parents=True, exist_ok=True)
        info = nif_static.convert(raw, str(dst), '../classic164_textures/x4plus')
        for tex in info['textures']:
            hits = [n for n in names if Path(n).stem.lower() == tex.lower() and Path(n).suffix.lower() in ['.tga', '.dds', '.bmp']]
            hits.sort(key=lambda n: (not n.startswith('items/'), n))
            if not hits:
                raise ValueError('Original texture missing: ' + tex)
            Image.open(io.BytesIO(z.read(names[hits[0]]))).convert('RGBA').save(src / (tex.lower() + '.png'))
        entry = data['models'][mid]
        entry['nif'] = name
        entry['file'] = folder + '/model.gltf'
        expected = {}
        for field, node in [('held', 'HELD'), ('held2', 'HELD2')]:
            frame = grip(raw, node)
            expected[field] = frame
            if frame is None:
                entry.pop(field, None)
            else:
                entry[field] = frame
        report.append({'model': int(mid), 'nif': name, **expected})
    build_zone.upscale_dir(src, args.output / 'weapons/classic164_textures/x4plus', factor_after=4)
    (args.output / 'weapons/weapons.json').write_text(json.dumps(data, indent=1), encoding='utf-8')
    (args.output / 'classic-claw-grips.json').write_text(json.dumps(report, indent=1), encoding='utf-8')
    print(json.dumps(report, indent=1))


if __name__ == '__main__':
    main()
