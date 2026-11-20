"""Build the Old Frontier dungeon chain after zones 245 and 247 have been converted.

Run with the normal zone pipeline inputs plus DAOC_164_ZIP. Pack the zone 248
scenes and export collision for 248/276/277 using the standard Godot tools.
"""

import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

from PIL import Image

import build_sounds
import build_zone
from daoc_mpk import read_mpk
import paths
from texture_index import TextureIndex


ZONE_ROOT = paths.ROOT / 'godot/content/zones'
ZONE_SOURCES = {276: 247, 277: 245}


class PeriodDungeonTextures:
    def __init__(self, archive, members):
        self.archive = archive
        self.members = members
        self.lookup = TextureIndex()

    def image(self, basename):
        hit = self.lookup.locate(basename)
        candidates = [hit[0].lower()] if hit and not hit[1] else []
        for directory in ('zones/dnifs', 'zones/nifs'):
            candidates.extend(f'{directory}/{basename}{ext}' for ext in ('.dds', '.tga', '.bmp'))
        for name in candidates:
            if name in self.members:
                with Image.open(io.BytesIO(self.archive.read(self.members[name]))) as picture:
                    return picture.convert('RGBA')
        return None


def zone_dir(zone_id):
    return ZONE_ROOT / f'zone{zone_id:03d}'


def read_json(file):
    return json.loads(file.read_text(encoding='utf-8-sig'))


def write_json(file, data):
    file.write_text(json.dumps(data, indent=1) + '\n', encoding='utf-8')


def copy_dungeon(server_id, source_id):
    # Keep region and zone IDs native to the server. The source and server
    # copies share the same bounds and local coordinate frame.
    source = zone_dir(source_id)
    dest = zone_dir(server_id)
    for file in ('zone.json', 'audio.json'):
        if not (source / file).is_file():
            raise FileNotFoundError(source / file)
    shutil.copytree(source, dest, dirs_exist_ok=True)
    zone = read_json(dest / 'zone.json')
    zone['id'] = server_id
    if server_id == 276:
        zone['name'] = 'Marfach Caverns'
    zone['spots'] = build_zone.entrance_spots(server_id)
    write_json(dest / 'zone.json', zone)
    audio = read_json(dest / 'audio.json')
    audio['zone'] = server_id
    write_json(dest / 'audio.json', audio)


def build_summoners_hall():
    zone = 248
    out = zone_dir(zone)
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(paths.need(paths.ZIP_164, 'ZIP_164')) as archive, tempfile.TemporaryDirectory() as tmp:
        members = {name.lower(): name for name in archive.namelist()}
        source = Path(tmp)
        dat_file = source / 'dat248.mpk'
        dat_file.write_bytes(archive.read(members['zones/zone248/dat248.mpk']))
        (source / 'sounds.dat').write_bytes(archive.read(members['zones/zone248/sounds.dat']))
        data = read_mpk(dat_file)
        pieces = source / 'Dnifs'
        pieces.mkdir()
        for chunk in data['dungeon.chunk'].decode('latin1').splitlines():
            if chunk.strip():
                name = Path(chunk.strip()).stem.lower() + '.npk'
                (pieces / name).write_bytes(archive.read(members['zones/dnifs/' + name]))
        build_zone.build_dungeon(zone, True, source, out, data, build_zone.sector_ini(data['sector.dat']),
                                 piece_dir=pieces, textures=PeriodDungeonTextures(archive, members))
        if read_json(out / 'zone.json')['missing_textures']:
            raise ValueError('Summoner’s Hall has unresolved period textures')
        build_sounds.build(zone, source_dir=source)


def main():
    index_path = ZONE_ROOT / 'zones_index.json'
    index = read_json(index_path)
    for server_id, source_id in ZONE_SOURCES.items():
        source, target = index[str(source_id)], index[str(server_id)]
        if any(source[key] != target[key] for key in ('x', 'y', 'w', 'h', 'type')):
            raise ValueError(f'Dungeon copy {source_id} does not match server zone {server_id}')
        copy_dungeon(server_id, source_id)
    build_summoners_hall()
    for zone in (248, *ZONE_SOURCES):
        subprocess.run([sys.executable, str(Path(__file__).with_name('build_npcs.py')), f'--zone={zone}'], check=True)
        index[str(zone)]['built'] = True
    write_json(index_path, index)

    content = ZONE_ROOT.parent
    sound_path = content / 'sounds/sounds.json'
    sound_index = read_json(sound_path)
    door_flags = sound_index.setdefault('zone_door_flag', {})
    for zone in (248, *ZONE_SOURCES):
        door_flags[str(zone)] = 0
    write_json(sound_path, sound_index)

    map_path = content / 'maps/maps.json'
    if map_path.exists():
        maps = read_json(map_path)
        for zone in (248, *ZONE_SOURCES):
            key = str(zone)
            # The later client's same-name maps are from different geometry.
            maps['zones'].setdefault(key, {'name': index[key]['name'], 'map': '', 'areas': [],
                                          'region_map': 0, 'desc': '', 'region': zone, 'type': 2})
        write_json(map_path, maps)


if __name__ == '__main__':
    main()
