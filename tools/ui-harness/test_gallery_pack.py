"""Tests of gallery_pack.py: the window frame is cut, and nothing of the picture with it. Needs Pillow:

    <backend venv>\\Scripts\\python.exe -B -m unittest discover -s tools/ui-harness -p "test_gallery_pack.py"
"""

import os
import shutil
import sys
import tempfile
import unittest

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gallery_pack  # noqa: E402


def framed(content, frame=8, light=(243, 243, 243)):
    """content inside a window frame as the harness's shots have it: light along the top, black elsewhere."""
    w, h = content.size
    shot = Image.new("RGB", (w + 2 * frame, h + 2 * frame), (0, 0, 0))
    ImageDraw.Draw(shot).rectangle((0, 0, shot.width - 1, frame - 1), fill=light)
    shot.paste(content, (frame, frame))
    return shot


class ClientAreaTests(unittest.TestCase):
    def test_the_frame_is_cut_exactly(self):
        content = Image.new("RGB", (400, 240), (40, 10, 20))
        out = gallery_pack.client_area(framed(content))
        self.assertEqual((400, 240), out.size)
        self.assertEqual((40, 10, 20), out.getpixel((0, 0)))

    def test_a_black_edge_in_the_picture_is_kept(self):
        # a letterboxed video: black bars at the picture's own sides and bottom
        content = Image.new("RGB", (400, 240), (0, 0, 0))
        ImageDraw.Draw(content).rectangle((60, 10, 340, 200), fill=(120, 120, 120))
        self.assertEqual((400, 240), gallery_pack.client_area(framed(content)).size)

    def test_a_shot_without_a_frame_stays_whole(self):
        content = Image.new("RGB", (400, 240), (0, 0, 0))
        ImageDraw.Draw(content).rectangle((0, 0, 399, 40), fill=(30, 30, 30))
        self.assertEqual((400, 240), gallery_pack.client_area(content).size)


class PackTests(unittest.TestCase):
    def test_the_newest_gallery_shot_of_each_name_is_packed_small_with_a_thumbnail(self):
        tmp = tempfile.mkdtemp(prefix="gallery-pack-")
        try:
            for run, name in (("a", "01-gallery-home-DriveIn.png"), ("b", "02-gallery-movie.png"), ("b", "03-zz-failure.png")):
                os.makedirs(os.path.join(tmp, "runs", run, "shots"), exist_ok=True)
                framed(Image.new("RGB", (640, 360), (90, 20, 40))).save(os.path.join(tmp, "runs", run, "shots", name))
            sizes = gallery_pack.pack(os.path.join(tmp, "runs"), os.path.join(tmp, "out"))
            self.assertEqual(["home-drive-in", "movie"], [s["name"] for s in sizes])
            self.assertTrue(all(s["bytes"] <= gallery_pack.LIMIT and s["size"] == [640, 360] for s in sizes))
            self.assertTrue(os.path.isfile(os.path.join(tmp, "out", "thumbs", "movie.webp")))
            self.assertTrue(os.path.isfile(os.path.join(tmp, "out", "contact.png")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
