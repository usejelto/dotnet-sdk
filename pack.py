#!/usr/bin/env python3
"""Build C19 twice in separate roots; normalize ZIP metadata, compare, then emit checksums."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent
dotnet = os.environ.get('DOTNET', 'dotnet')
env = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}
version = ET.parse(root / 'Jelto/Jelto.csproj').findtext('.//Version')
package_name = f'Jelto.{version}.nupkg'

def build(directory, snapshot):
    shutil.copytree(snapshot, directory)
    subprocess.run([dotnet, 'pack', str(directory / 'Jelto'), '-c', 'Release', '-o', str(directory / 'out'),
                    '-p:UseSharedCompilation=false'], env=env, check=True)
    package = directory / 'out' / package_name
    canonical = directory / package_name
    with zipfile.ZipFile(package) as source, zipfile.ZipFile(canonical, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        entries = {}
        for name in source.namelist():
            data = source.read(name)
            if name.endswith('.psmdcp'):
                name = 'package/services/metadata/core-properties/jelto.psmdcp'
            elif name == '_rels/.rels':
                relationships = ET.fromstring(data)
                for relationship in relationships:
                    if relationship.get('Type', '').endswith('/metadata/core-properties'):
                        relationship.set('Target', '/package/services/metadata/core-properties/jelto.psmdcp')
                    relationship.set('Id', 'R' + hashlib.sha256(relationship.get('Target').encode()).hexdigest()[:16])
                data = ET.tostring(relationships, encoding='utf-8', xml_declaration=True)
            entries[name] = data
        for name, data in sorted(entries.items()):
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            target.writestr(info, data, compresslevel=9)
    return canonical

with tempfile.TemporaryDirectory(prefix='jelto-dotnet-pack-') as scratch:
    scratch = Path(scratch).resolve()
    snapshot = scratch / 'source'
    snapshot.mkdir()
    shutil.copy(root / 'Directory.Build.props', snapshot)
    shutil.copy(root / 'README.md', snapshot)
    shutil.copy(root / 'LICENSE', snapshot)
    shutil.copytree(root / 'Jelto', snapshot / 'Jelto', ignore=shutil.ignore_patterns('bin', 'obj'))
    first, second = [build(Path(scratch) / part, snapshot) for part in ('first', 'second')]
    if first.read_bytes() != second.read_bytes():
        with zipfile.ZipFile(first) as a, zipfile.ZipFile(second) as b:
            different = [name for name in a.namelist() if name not in b.namelist() or a.read(name) != b.read(name)]
        raise SystemExit('C19 failed: non-identical packages: ' + ', '.join(different))
    with zipfile.ZipFile(first) as package:
        assert {'lib/net8.0/Jelto.dll', 'lib/net8.0/Jelto.xml', 'README.md', 'LICENSE', 'Jelto.nuspec'} <= set(package.namelist())
        assert package.read('LICENSE') == (root / 'LICENSE').read_bytes(), 'MIT license text differs'
        assert b'<dependency ' not in package.read('Jelto.nuspec'), 'runtime dependency found'
    artifacts = root / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    output = artifacts / first.name
    shutil.copy(first, output)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    (artifacts / 'CHECKSUMS').write_text(f'{digest}  {output.name}\n')
    print(f'C19 PASS: isolated builds are byte-identical; {digest}  {output}')
