"""Minimal PE32+ DLLs for the release tools' tests: an export directory and an import directory, nothing else.

pefile reads them like a linker's output; Windows would not load them (no code, no relocations), and nothing runs them.
"""

import struct

SECTION_RVA = 0x1000
FILE_ALIGN = 0x200


def write_dll(path, name, exports=(), imports=None):
    """Writes a DLL called `name` exporting the functions `exports` and importing `imports`: {dll: [function name, or
    an int for an import by ordinal]}."""
    body = bytearray()

    def add(data, align=8):
        while len(body) % align:
            body.append(0)
        rva = SECTION_RVA + len(body)
        body.extend(data)
        return rva

    code = add(b"\xc3" * 16)  # every exported function's address (outside the export directory: not a forwarder)
    export_dir = (0, 0)
    if exports:
        names = sorted(exports)
        dll_name = add(name.encode("ascii") + b"\0", 1)
        name_rvas = [add(n.encode("ascii") + b"\0", 1) for n in names]
        functions = add(b"".join(struct.pack("<I", code) for _ in names), 4)
        pointers = add(b"".join(struct.pack("<I", r) for r in name_rvas), 4)
        ordinals = add(b"".join(struct.pack("<H", i) for i in range(len(names))), 2)
        directory = struct.pack("<IIHHIIIIIII", 0, 0, 0, 0, dll_name, 1, len(names), len(names), functions, pointers, ordinals)
        export_dir = (add(directory, 4), len(directory))
    import_dir = (0, 0)
    if imports:
        blocks = []
        for dll, functions in imports.items():
            dll_rva = add(dll.encode("ascii") + b"\0", 2)
            thunks = b""
            for f in functions:
                if isinstance(f, int):
                    thunks += struct.pack("<Q", (1 << 63) | f)
                else:
                    thunks += struct.pack("<Q", add(struct.pack("<H", 0) + f.encode("ascii") + b"\0", 2))
            thunks += struct.pack("<Q", 0)
            blocks.append((add(thunks, 8), dll_rva, add(thunks, 8)))
        descriptors = b"".join(struct.pack("<IIIII", ilt, 0, 0, dll_rva, iat) for ilt, dll_rva, iat in blocks) + bytes(20)
        import_dir = (add(descriptors, 4), len(descriptors))

    raw_size = -(-len(body) // FILE_ALIGN) * FILE_ALIGN
    image_size = SECTION_RVA + -(-len(body) // 0x1000) * 0x1000
    dos = bytearray(64)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 64)
    coff = struct.pack("<HHIIIHH", 0x8664, 1, 0, 0, 0, 240, 0x2022)  # x64; 1 section; executable, large address, DLL
    optional = struct.pack("<HBBIIIII", 0x20B, 14, 0, 0, raw_size, 0, 0, SECTION_RVA)
    optional += struct.pack("<QIIHHHHHHIIIIHHQQQQII", 0x180000000, 0x1000, FILE_ALIGN, 6, 0, 0, 0, 6, 0, 0, image_size,
                            FILE_ALIGN, 0, 2, 0x160, 0x100000, 0x1000, 0x100000, 0x1000, 0, 16)
    directories = [(0, 0)] * 16
    directories[0], directories[1] = export_dir, import_dir
    optional += b"".join(struct.pack("<II", rva, size) for rva, size in directories)
    section = struct.pack("<8sIIIIIIHHI", b".rdata", len(body), SECTION_RVA, raw_size, FILE_ALIGN, 0, 0, 0, 0, 0x40000040)
    headers = bytes(dos) + b"PE\0\0" + coff + optional + section
    with open(path, "wb") as fh:
        fh.write(headers.ljust(FILE_ALIGN, b"\0") + bytes(body).ljust(raw_size, b"\0"))
