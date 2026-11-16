"""Zips a folder with the folder itself as the one top-level entry, files in sorted order, '/' separators.

    python -I zipdir.py <folder> <out.zip>
"""

import os
import sys
import zipfile


def main(folder, out):
    folder = os.path.abspath(folder)
    top = os.path.basename(folder)
    paths = []
    for root, dirs, files in os.walk(folder):
        dirs.sort()
        for name in sorted(files):
            paths.append(os.path.join(root, name))
    with zipfile.ZipFile(out + ".part", "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as zf:
        for path in paths:
            arc = top + "/" + os.path.relpath(path, folder).replace(os.sep, "/")
            zf.write(path, arc)
    os.replace(out + ".part", out)
    print(f"{out}: {len(paths)} files")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
