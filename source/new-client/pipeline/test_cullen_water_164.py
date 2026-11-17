"""Cullen's period banks, deliberate unused rows, and isolation from other zones.
    python -m unittest discover -s pipeline -p test_cullen_water_164.py -v
"""
import configparser
import unittest

import numpy as np

from cullen_water_164 import restore_mask


# Declared bank pairs from the real 1.64 dat205.mpk, not the unused rows after bankpoints.
BANKS = (
    ([(42, 116), (41, 93), (45, 64), (48, 44)], [(90, 117), (99, 97), (97, 63), (91, 44)]),
    ([(102, 102), (102, 80), (105, 60)], [(157, 102), (157, 80), (156, 59)]),
    ([(79, 217), (85, 183), (80, 157), (77, 135), (101, 106)], [(129, 220), (136, 189), (136, 162), (154, 137), (151, 104)]),
    ([(43, 203), (41, 173), (43, 150), (47, 139)], [(78, 201), (82, 174), (77, 147), (72, 139)]),
    ([(152, 227), (140, 201), (141, 164), (168, 117), (170, 89), (184, 48)],
     [(200, 230), (209, 206), (204, 166), (217, 122), (222, 89), (215, 47)]),
)


def period_sector():
    cp = configparser.ConfigParser()
    for i, (left, right) in enumerate(BANKS):
        section = f'river{i:02d}'
        cp[section] = {'bankpoints': str(len(left)), 'height': '4910', 'type': 'LAKE'}
        for side, points in (('left', left), ('right', right)):
            for j, (x, y) in enumerate(points):
                cp[section][f'{side}{j:02d}'] = f'{x},{y},0'
    return cp


class CullenWater(unittest.TestCase):
    def setUp(self):
        self.dry = np.full((256, 256), 255, dtype=np.uint8)
        self.sector = period_sector()

    def test_all_five_recorded_lakes_have_water_at_their_centres(self):
        mask = restore_mask(205, self.dry, self.sector)
        for body, (x, y) in enumerate(((65, 80), (130, 80), (110, 170), (60, 175), (190, 180))):
            with self.subTest(body=body):
                self.assertEqual(mask[y, x], body)
        self.assertTrue(np.all(self.dry == 255), 'Do not mutate the original mask')

    def test_land_between_lakes_and_outside_the_banks_stays_dry(self):
        mask = restore_mask(205, self.dry, self.sector)
        for x, y in ((100, 80), (39, 80), (120, 20), (250, 250), (5, 5)):
            with self.subTest(x=x, y=y):
                self.assertEqual(mask[y, x], 255)

    def test_unused_bank_rows_cannot_flood_the_zone(self):
        expected = restore_mask(205, self.dry, self.sector)
        # The stock river00 really has stale rows beyond its four declared pairs.
        self.sector['river00'].update({'left04': '0,0,0', 'right04': '255,0,0',
                                       'left05': '0,255,0', 'right05': '255,255,0'})
        np.testing.assert_array_equal(restore_mask(205, self.dry, self.sector), expected)

    def test_every_other_zone_keeps_its_exact_existing_mask(self):
        for zone in range(400):
            if zone != 205:
                self.assertIs(restore_mask(zone, self.dry, None), self.dry)

    def test_existing_water_is_never_overwritten(self):
        painted = self.dry.copy()
        painted[1, 1] = 3
        self.assertIs(restore_mask(205, painted, None), painted)

    def test_repeated_conversion_is_unchanged(self):
        mask = restore_mask(205, self.dry, self.sector)
        self.assertIs(restore_mask(205, mask, self.sector), mask)

    def test_changed_or_incomplete_period_data_is_rejected(self):
        self.sector['river02']['height'] = '5000'
        with self.assertRaises(ValueError):
            restore_mask(205, self.dry, self.sector)
        self.assertTrue(np.all(self.dry == 255))
        self.sector['river02']['height'] = '4910'
        del self.sector['river02']['left02']
        with self.assertRaises(KeyError):
            restore_mask(205, self.dry, self.sector)


if __name__ == '__main__':
    unittest.main()
