#!/usr/bin/env python3
"""Sign a release manifest locally. The Windows package stays on its build runner."""
import argparse, base64, hashlib, json, re, subprocess
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('--version', required=True); p.add_argument('--mac-zip', type=Path, required=True)
p.add_argument('--mac-dmg', type=Path)
p.add_argument('--windows-sha256', required=True); p.add_argument('--windows-size', type=int, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
if not re.fullmatch(r'[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}', a.version) or not re.fullmatch(r'[0-9a-f]{64}', a.windows_sha256):
    raise SystemExit('Invalid version or Windows checksum')
base = f'https://github.com/ielfstone/unictrl-oss/releases/download/v{a.version}/'
release = {'version': a.version,
    'macos': {'url': base + a.mac_zip.name, 'sha256': hashlib.sha256(a.mac_zip.read_bytes()).hexdigest(), 'size': a.mac_zip.stat().st_size},
    'windows': {'url': base + f'Unictrl-{a.version}-windows-x64-portable.zip', 'sha256': a.windows_sha256, 'size': a.windows_size}}
if a.mac_dmg:
    release['macosDmg'] = {'url': base + a.mac_dmg.name, 'sha256': hashlib.sha256(a.mac_dmg.read_bytes()).hexdigest(), 'size': a.mac_dmg.stat().st_size}
payload = json.dumps(release, separators=(',', ':')).encode()
key = Path.home() / 'Library/Application Support/UnictrlBuild/local-signing-key.pem'
result = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key)], input=payload, capture_output=True)
if result.returncode: raise SystemExit('Local manifest signing failed')
a.output.write_text(json.dumps({'payload': base64.b64encode(payload).decode(), 'signature': base64.b64encode(result.stdout).decode()}, indent=2) + '\n')
print('Signed release manifest:', a.output)
