"""
Сборка wgt TorrStream для Samsung Tizen.

  python tools/build.py [--version 1.0.1] [--out dist] [--sign ПРОФИЛЬ] [--probe]

Версию подставляет в config.xml (в CI — из тега). Результат —
TorrStream-Tizen_<версия>.wgt и копия с постоянным именем TorrStream-Tizen.wgt.

Без --sign пакет НЕ подписан: ставить его на телевизор всё равно нужно со
своим сертификатом Samsung (он привязан к DUID телевизора), и Apps2Samsung,
и Tizen Studio подписывают wgt заново при установке. С --sign собирает
Tizen CLI (`tizen package -t wgt -s ПРОФИЛЬ`, профиль из Certificate
Manager) — tizen должен быть в PATH.

--probe собирает проверочный пакет из probe/ (TorrStream-Tizen-Probe.wgt,
свой id — ставится рядом с TorrStream): что может страница внутри wgt.
"""
import argparse, os, re, shutil, subprocess, tempfile, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

ap = argparse.ArgumentParser()
ap.add_argument('--version', help='версия пакета (по умолчанию из app/config.xml)')
ap.add_argument('--out', default=os.path.join(ROOT, 'dist'))
ap.add_argument('--sign', metavar='PROFILE', help='подписать профилем сертификата Tizen CLI')
ap.add_argument('--probe', action='store_true', help='проверочный пакет из probe/')
args = ap.parse_args()

work = tempfile.mkdtemp()
app = os.path.join(work, 'app')
shutil.copytree(os.path.join(ROOT, 'probe' if args.probe else 'app'), app)
if args.probe:
    shutil.copyfile(os.path.join(ROOT, 'app', 'icon.png'), os.path.join(app, 'icon.png'))

cfg_path = os.path.join(app, 'config.xml')
cfg = open(cfg_path, encoding='utf-8').read()
if args.version:
    # Tizen принимает только x.y.z из цифр
    if not re.match(r'^\d+\.\d+\.\d+$', args.version):
        raise SystemExit('версия должна быть вида 1.0.1: ' + args.version)
    cfg = re.sub(r'(<widget\b[^>]*\bversion=")[^"]*(")', r'\g<1>' + args.version + r'\g<2>', cfg, count=1)
    open(cfg_path, 'w', encoding='utf-8').write(cfg)
version = re.search(r'<widget\b[^>]*\bversion="([^"]+)"', cfg).group(1)

os.makedirs(args.out, exist_ok=True)
base = 'TorrStream-Tizen-Probe' if args.probe else 'TorrStream-Tizen'
name = '%s_%s.wgt' % (base, version)
target = os.path.join(args.out, name)

if args.sign:
    tizen = shutil.which('tizen') or shutil.which('tizen.bat')
    if not tizen:
        raise SystemExit('tizen CLI не найден в PATH (Tizen Studio: tools/ide/bin)')
    subprocess.check_call([tizen, 'package', '-t', 'wgt', '-s', args.sign, '--', app])
    built = [f for f in os.listdir(app) if f.endswith('.wgt')]
    shutil.move(os.path.join(app, built[0]), target)
else:
    # wgt — обычный zip; config.xml в корне
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        for folder, _, files in os.walk(app):
            for f in sorted(files):
                full = os.path.join(folder, f)
                z.write(full, os.path.relpath(full, app).replace(os.sep, '/'))

shutil.copyfile(target, os.path.join(args.out, base + '.wgt'))
shutil.rmtree(work, ignore_errors=True)
print('==', target, '(подписан)' if args.sign else '(без подписи)')
