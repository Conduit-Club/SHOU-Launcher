#!/usr/bin/env python3
"""Reproduce the portable reference index from the checked-in WPF gzip/protobuf asset."""
import gzip
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = root / 'Plain Craft Launcher 2/Resources/mcmod.buf'

def varint(data, offset):
    value = shift = 0
    while shift < 70:
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
        shift += 7
    raise ValueError('Invalid protobuf varint')

def fields(data):
    offset = 0
    while offset < len(data):
        key, offset = varint(data, offset)
        field, wire = key >> 3, key & 7
        if wire == 0:
            value, offset = varint(data, offset)
        elif wire == 2:
            size, offset = varint(data, offset)
            value = data[offset:offset + size]
            if len(value) != size:
                raise ValueError('Truncated protobuf value')
            offset += size
        else:
            raise ValueError(f'Unexpected wire type {wire}')
        yield field, value

entries = []
for field, record in fields(gzip.decompress(source.read_bytes())):
    if field != 1 or not isinstance(record, bytes):
        raise ValueError('Unexpected database record')
    values = dict(fields(record))
    entries.append(dict(Id=values.get(1, 0), Name=values.get(2, b'').decode(),
                        CurseForge=values.get(3, b'').decode(), Modrinth=values.get(4, b'').decode()))
output = root / 'src/SHOU.Launcher.Core/Resources/mcmod-index.json'
output.write_text(json.dumps(entries, ensure_ascii=False, separators=(',', ':')) + '\n')
print(f'{len(entries)} entries written')
