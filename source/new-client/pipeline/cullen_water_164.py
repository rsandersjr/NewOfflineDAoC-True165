"""Restore Bog of Cullen's five lake masks from the 1.64 sector.dat bank strips.

Its original water.pcx is entirely 255 (dry). The original client also reads the
river bank strips (game.dll 0x463f4c-0x46407d); our renderer needs their coverage
in the mask. This conversion is ONLY for zone 205 with an entirely dry mask.
The existing water renderer, shore protections, heights and materials stay intact.

Preview or patch an existing converted zone without rebuilding its other assets:
    python cullen_water_164.py --client-164 <DAoC 1.64.zip> --zone-dir <zone205>
    ... --apply --backup-dir <backup>                 # install, with game closed
    ... --apply --output-dir <staging>                # prepare separate data
"""
import argparse
import configparser
import io
import json
import shutil
import tempfile
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image


ZONE = 205
COUNTS = (4, 3, 5, 4, 6)


def restore_mask(zone_id, source, sector):
    """Rasterize the recorded bank strips at cell centres, keeping all other masks.

    Bank coordinates are terrain-grid coordinates (256 DAoC units per cell).
    Only the declared bankpoints count: river00 has unused old rows after it.
    Equal-height overlaps belong to the lower body ID, as in ZoneLoader.
    """
    if zone_id != ZONE or np.any(source != 255):
        return source
    if source.shape != (256, 256) or source.dtype != np.uint8:
        raise ValueError('Cullen requires its original 256 x 256 byte mask')
    if sector.has_section('river05'):
        raise ValueError('Cullen has five period lakes; unexpected extra body')
    out = source.copy()
    yy, xx = np.mgrid[:256, :256].astype(float) + 0.5

    def triangle(a, b, c):
        def edge(p, q):
            return (xx - p[0]) * (q[1] - p[1]) - (yy - p[1]) * (q[0] - p[0])
        area = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
        if area == 0:
            return np.zeros(source.shape, dtype=bool)
        e0, e1, e2 = edge(a, b), edge(b, c), edge(c, a)
        return ((e0 >= 0) & (e1 >= 0) & (e2 >= 0)) | ((e0 <= 0) & (e1 <= 0) & (e2 <= 0))

    for body, count in enumerate(COUNTS):
        section = sector[f'river{body:02d}']
        if (section.getint('bankpoints') != count or section.getfloat('height') != 4910
                or section.get('type', '').upper() != 'LAKE'):
            raise ValueError(f'Cullen river{body:02d} does not match the period lake definition')
        banks = []
        for bank in ('left', 'right'):
            points = []
            for i in range(count):
                parts = section[f'{bank}{i:02d}'].split(';', 1)[0].split(',')
                point = tuple(int(v.strip()) for v in parts[:2])
                if len(point) != 2 or not all(0 <= v <= 255 for v in point):
                    raise ValueError(f'Invalid Cullen bank point: {bank}{i:02d}')
                points.append(point)
            banks.append(points)
        left, right = banks
        covered = np.zeros(source.shape, dtype=bool)
        for i in range(count - 1):
            covered |= triangle(left[i], right[i], left[i + 1])
            covered |= triangle(right[i], right[i + 1], left[i + 1])
        out[covered & (out == 255)] = body
        if not np.any(out == body):
            raise ValueError(f'Cullen lake {body} has no usable bank coverage')
    return out


def original_data(archive):
    """Read the stock archive; extract just dat205.mpk to a disposable directory."""
    from daoc_mpk import read_mpk
    with zipfile.ZipFile(archive) as client:
        name = next(n for n in client.namelist() if n.lower() == 'zones/zone205/dat205.mpk')
        with tempfile.TemporaryDirectory(prefix='cullen-164-') as tmp:
            dat = Path(tmp) / 'dat205.mpk'
            dat.write_bytes(client.read(name))
            files = read_mpk(dat)
    sector = configparser.ConfigParser(strict=False, interpolation=None, inline_comment_prefixes=(';',))
    sector.read_string(files['sector.dat'].decode('latin1'))
    water = np.asarray(Image.open(io.BytesIO(files['water.pcx'])), dtype=np.uint8)
    return sector, water


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--client-164', required=True, type=Path)
    ap.add_argument('--zone-dir', required=True, type=Path)
    ap.add_argument('--output-dir', type=Path)
    ap.add_argument('--backup-dir', type=Path)
    ap.add_argument('--apply', action='store_true')
    args = ap.parse_args()
    sector, original = original_data(args.client_164)
    restored = restore_mask(ZONE, original, sector)
    source = args.zone_dir
    info = json.loads((source / 'zone.json').read_text(encoding='utf-8-sig'))
    current = np.asarray(Image.open(source / info['water_mask']))
    if info['id'] != ZONE or info['water_mask'] != 'water.png' or len(info['water']) != 5:
        raise ValueError('Not the expected converted Cullen zone')
    if not (np.array_equal(current, original) or np.array_equal(current, restored)):
        raise ValueError('Converted mask contains other changes; refusing to overwrite it')
    for body in info['water']:
        if float(body['height']) != 122.75 or body['id'] not in range(5):
            raise ValueError('Converted Cullen water levels or body IDs differ')
        body['cells'] = int(np.count_nonzero(restored == body['id']))
    encoded = json.dumps(info, indent=1).encode('utf-8')
    print('Cullen lake cells:', [(b['name'], b['cells']) for b in info['water']])
    if not args.apply:
        print('Preview only; nothing written.')
        return
    target = args.output_dir or source
    if target.resolve() == source.resolve():
        if args.backup_dir is None:
            raise ValueError('--backup-dir is required for installation')
        if np.array_equal(current, restored) and (source / 'zone.json').read_bytes() == encoded:
            print('Already installed; nothing written.')
            return
        args.backup_dir.mkdir(parents=True, exist_ok=False)
        for name in ('zone.json', 'water.png'):
            shutil.copy2(source / name, args.backup_dir / name)
    target.mkdir(parents=True, exist_ok=True)
    Image.fromarray(restored).save(target / 'water.png')
    (target / 'zone.json').write_bytes(encoded)
    print('Wrote Cullen water data to', target)


if __name__ == '__main__':
    main()
