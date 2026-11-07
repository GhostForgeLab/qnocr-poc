"""Audit a portable ZIP without executing it or printing any private values."""
import hashlib
import ctypes
import json
import pathlib
import re
import struct
import sys
import zipfile


def private_values(project):
    """Read comparison needles in memory only; never print or write credentials/data."""
    values = set()
    root = project / 'Output' / 'DesktopClient'
    settings_path = root / 'settings.json'
    if settings_path.exists():
        import base64
        from ctypes import wintypes

        class Blob(ctypes.Structure):
            _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]

        decrypt = ctypes.windll.crypt32.CryptUnprotectData
        decrypt.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                            ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
        decrypt.restype = wintypes.BOOL
        free = ctypes.windll.kernel32.LocalFree
        free.argtypes = [ctypes.c_void_p]
        free.restype = ctypes.c_void_p
        settings = json.loads(settings_path.read_text(encoding='utf-8-sig'))
        for field in ('AiSecret', 'JevSecret', 'OnlineKnowledgeSecret'):
            encrypted = settings.get(field, '')
            if not encrypted:
                continue
            values.add(encrypted)
            raw = base64.b64decode(encrypted)
            buffer = (ctypes.c_ubyte * len(raw)).from_buffer_copy(raw)
            incoming, outgoing = Blob(len(raw), buffer), Blob()
            if not decrypt(ctypes.byref(incoming), None, None, None, None, 1, ctypes.byref(outgoing)):
                raise RuntimeError('Cannot verify key exclusion without the normal Windows user encryption context')
            try:
                values.add(ctypes.string_at(outgoing.data, outgoing.size).decode('utf-8'))
            finally:
                ctypes.memset(outgoing.data, 0, outgoing.size)
                free(outgoing.data)
                ctypes.memset(buffer, 0, len(raw))
    # Stable IDs and distinctive private text catch accidentally embedded records.
    for name in ('business.json', 'products.json'):
        path = root / name
        if not path.exists():
            continue
        payload = json.loads(path.read_text(encoding='utf-8-sig'))
        if isinstance(payload, list):
            payload = {'Products': payload}
        for shop in payload.get('Shops', []):
            values.update(str(shop.get(key, '')) for key in ('Id', 'BindingKey', 'DisplayName'))
        for product in payload.get('Products', []):
            values.update(str(product.get(key, '')) for key in ('ShopId', 'ProductId', 'Name'))
        for record in payload.get('Candidates', []) + payload.get('Rules', []):
            values.update(str(record.get(key, '')) for key in ('Id', 'CandidateId', 'SourceKey', 'SampleHash'))
    for path in [root / 'inbox.json', *root.glob('windows/*/inbox.json')]:
        if not path.exists():
            continue
        for record in json.loads(path.read_text(encoding='utf-8-sig')):
            values.add(record.get('Buyer', ''))
            if len(record.get('Text', '')) >= 40:
                values.add(record['Text'])
    values.update(['tb300116357', 'oswaldsterling', '985503282040'])
    return [value.encode(encoding) for value in values if len(value) >= 8
            for encoding in ('utf-8', 'utf-16le')]


def imports(data):
    if data[:2] != b'MZ':
        return []
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Invalid PE')
    sections = struct.unpack_from('<H', data, pe + 6)[0]
    opt_size = struct.unpack_from('<H', data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', data, opt)[0]
    table = opt + (112 if magic == 0x20B else 96)
    section_table = opt + opt_size

    def offset(rva):
        for i in range(sections):
            entry = section_table + 40 * i
            size, address, raw_size, raw = struct.unpack_from('<IIII', data, entry + 8)
            if address <= rva < address + max(size, raw_size):
                return raw + rva - address
        return rva

    def name(rva):
        start = offset(rva)
        return data[start:data.index(b'\0', start)].decode('ascii').lower()

    dependencies = []
    import_rva = struct.unpack_from('<I', data, table + 8)[0]
    if import_rva:
        cursor = offset(import_rva)
        while any(data[cursor:cursor + 20]):
            dependencies.append(name(struct.unpack_from('<I', data, cursor + 12)[0]))
            cursor += 20
    # Delay-loaded imports are also required when their code path is first used.
    delay_rva = struct.unpack_from('<I', data, table + 13 * 8)[0]
    if delay_rva:
        cursor = offset(delay_rva)
        while any(data[cursor:cursor + 32]):
            attributes, name_rva = struct.unpack_from('<II', data, cursor)
            if attributes & 1:
                dependencies.append(name(name_rva))
            cursor += 32
    return dependencies


def main():
    archive = pathlib.Path(sys.argv[1])
    project = pathlib.Path(__file__).resolve().parents[1]
    needles = private_values(project)
    private_pattern = re.compile(b'|'.join(re.escape(n) for n in needles)) if needles else None
    with zipfile.ZipFile(archive) as package:
        members = [item for item in package.infolist() if not item.is_dir()]
        roots = {item.filename.split('/')[0] for item in members}
        assert len(roots) == 1, 'Expected one top-level folder'
        prefix = next(iter(roots)) + '/'
        names = {item.filename[len(prefix):]: item for item in members}
        assert len(names) == len(members), 'Duplicate ZIP entries'
        forbidden_dirs = {'data', 'output', 'smokedata', 'captures', 'windows', 'reviews',
                          'reply-index', 'knowledge-advice', 'business-backups', 'learning', '.git', '.tools'}
        for relative in names:
            path = pathlib.PurePosixPath(relative)
            assert not path.is_absolute() and '..' not in path.parts
            assert not forbidden_dirs.intersection(part.lower() for part in path.parts[:-1]), 'Private directory in ZIP'
            assert path.suffix.lower() not in {'.db', '.sqlite', '.sqlite3', '.jsonl', '.log', '.pdb', '.cs', '.png', '.jpg', '.env',
                                               '.qnbak', '.bak', '.zip', '.7z', '.rar', '.ps1', '.vbs'}, 'Unexpected data file'
            if path.suffix == '.json':
                assert path.name.endswith(('.deps.json', '.runtimeconfig.json')), 'Unexpected JSON'
        manifest = json.loads(package.read(prefix + '文件校验清单.txt').decode('utf-8-sig'))
        assert {entry['Path'] for entry in manifest} == set(names) - {'文件校验清单.txt'}, 'Manifest membership mismatch'
        for entry in manifest:
            content = package.read(prefix + entry['Path'])
            assert len(content) == entry['Bytes'] and hashlib.sha256(content).hexdigest().upper() == entry['SHA256'], 'Manifest hash mismatch'
            assert private_pattern is None or not private_pattern.search(content), 'Private credential or record found in package content'
        assert private_pattern is None or not private_pattern.search(package.read(prefix + '文件校验清单.txt')), 'Private token in manifest'
        for host in ('QNAgent', 'qnocr-poc'):
            runtime = json.loads(package.read(prefix + host + '.runtimeconfig.json'))['runtimeOptions']
            assert runtime.get('includedFrameworks') and not runtime.get('frameworks') and not runtime.get('framework'), 'Host requires global runtime'
        local_dlls = {pathlib.PurePosixPath(name).name.lower() for name in names if '/' not in name}
        import_count = 0
        missing = set()
        for relative in names:
            if '/' in relative or not relative.lower().endswith(('.exe', '.dll')):
                continue
            for dependency in imports(package.read(prefix + relative)):
                import_count += 1
                if dependency in local_dlls or dependency.startswith(('api-ms-', 'ext-ms-')):
                    continue
                if dependency.startswith(('msvcp140', 'vcruntime140', 'concrt140')):
                    missing.add(dependency)
                elif not (pathlib.Path('C:/Windows/System32') / dependency).exists():
                    missing.add(dependency)
        assert not missing, 'Unresolved native dependencies: ' + ', '.join(sorted(missing))
        print(f'PASS: {len(members)} ZIP files match the SHA256 manifest')
        print('PASS: no user data, chat, business backups, learning records, screenshots or debug symbols')
        print('PASS: all ZIP payloads checked against local plain/encrypted keys and private record identifiers (no values printed)')
        print('PASS: development replay identifiers absent from application binaries')
        print('PASS: desktop and OCR worker are self-contained')
        print(f'PASS: {import_count} native import references resolve to package or Windows APIs')
        print(f'ZIP bytes: {archive.stat().st_size}')


if __name__ == '__main__':
    main()
