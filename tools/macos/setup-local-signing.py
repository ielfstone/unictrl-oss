#!/usr/bin/env python3
"""One persistent personal signing identity. Private material stays outside the repository."""
from pathlib import Path
import json, os, secrets, subprocess, tempfile

home = Path.home() / 'Library/Application Support/UnictrlBuild'
home.mkdir(parents=True, exist_ok=True, mode=0o700)
os.chmod(home, 0o700)
os.umask(0o077)
key = home / 'local-signing-key.pem'
cert = home / 'local-signing-cert.pem'
chain = home / 'Signing.keychain-db'
password_file = home / 'keychain-password'
def run(*args, capture=False):
    result = subprocess.run(args, stdout=subprocess.PIPE if capture else subprocess.DEVNULL, stderr=subprocess.PIPE)
    if result.returncode:
        raise SystemExit(result.stderr.decode(errors='replace'))
    return result.stdout
if not key.exists():
    run('openssl', 'req', '-new', '-x509', '-newkey', 'rsa:3072', '-nodes', '-sha256', '-days', '3650',
        '-subj', '/CN=Unictrl Personal Development/O=Unictrl Personal Fork',
        '-addext', 'basicConstraints=critical,CA:FALSE', '-addext', 'keyUsage=critical,digitalSignature',
        '-addext', 'extendedKeyUsage=codeSigning', '-keyout', str(key), '-out', str(cert))
    password_file.write_text(secrets.token_hex(24))
    run('security', 'create-keychain', '-p', password_file.read_text(), str(chain))
    run('security', 'set-keychain-settings', '-lut', '21600', str(chain))
run('security', 'unlock-keychain', '-p', password_file.read_text(), str(chain))
existing = subprocess.run(['security', 'find-certificate', '-c', 'Unictrl Personal Development', str(chain)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
if existing.returncode:
    with tempfile.TemporaryDirectory(dir=home) as temp:
        p12 = Path(temp) / 'identity.p12'
        env = os.environ.copy(); env['UNICTRL_P12_PASS'] = secrets.token_hex(24)
        result = subprocess.run(['openssl', 'pkcs12', '-export', '-inkey', str(key), '-in', str(cert), '-out', str(p12),
                        '-keypbe', 'PBE-SHA1-3DES', '-certpbe', 'PBE-SHA1-3DES', '-macalg', 'sha1',
                        '-passout', 'env:UNICTRL_P12_PASS'], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        if result.returncode: raise SystemExit(result.stderr.decode(errors='replace'))
        run('security', 'import', str(p12), '-k', str(chain), '-P', env['UNICTRL_P12_PASS'], '-T', '/usr/bin/codesign')
    run('security', 'set-key-partition-list', '-S', 'apple-tool:,apple:', '-s', '-k', password_file.read_text(), str(chain))
fingerprint = run('openssl', 'x509', '-in', str(cert), '-noout', '-fingerprint', '-sha1', capture=True).decode().strip().split('=')[1].replace(':', '')
print(json.dumps({'identity': fingerprint, 'keychain': str(chain), 'certificate': str(cert)}, indent=2))
