"""
Сборка wgt TorrStream для Samsung Tizen.

  python tools/build.py [--version 1.0.1] [--out dist] [--sign ПРОФИЛЬ] [--probe]
  python tools/build.py --ffprobe [--lib libtsprobe.so] [--version 1.0.1]

Версию подставляет в config.xml (в CI — из тега). Результат —
<имя>_<версия>.wgt и копия с постоянным именем <имя>.wgt.

Без ключей — TorrStream-Tizen.wgt: только запускалка из app/, без .NET.

--ffprobe — отдельный пакет TorrStream-ffprobe.wgt (свой id, TorrFfprb1), чтобы
служба не раздувала сам TorrStream. Он гибридный — как TorrServer от
no-cover/torr-streaming: веб-часть из ffprobe-app/ (запуск службы, её состояние)
и .NET-служба ffprobe (ffprobe-service/, tools/build_ffprobe.py). Раскладка такая
же, как у них:
  config.xml, tizen-manifest.xml   — в корне (package службы = package веб-приложения)
  bin/  shared/res/  res/ffprobe   — .NET-служба (из её .tpk)
  res/wgt/                         — веб-часть, её config.xml тоже здесь
Подписей внутри нет: пакет подписывает тот, кто ставит (Apps2Samsung, установщик
с телефона, Tizen Studio) своим сертификатом Samsung — он привязан к DUID
телевизора. Нужен .NET SDK (Tizen.NET.Sdk скачается сам).
TorrStream находит службу сам — по HTTP на IP телевизора:8095.

--lib libtsprobe.so — тот же пакет для Tizen 5.5 (TorrStream-ffprobe-5.5.wgt): там
службе нельзя запускать программы, поэтому вместо res/ffprobe — libtsprobe.so,
урезанный FFmpeg внутри процесса службы (tools/build_tsprobe.sh, ffprobe-service/native).

--sign — обычный wgt через Tizen CLI (`tizen package -t wgt -s ПРОФИЛЬ`).
--probe — проверочный пакет из probe/ (TorrStream-Tizen-Probe.wgt, свой id).
"""
import argparse, os, re, shutil, subprocess, sys, tempfile, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

ap = argparse.ArgumentParser()
ap.add_argument('--version', help='версия пакета (по умолчанию из config.xml)')
ap.add_argument('--out', default=os.path.join(ROOT, 'dist'))
ap.add_argument('--sign', metavar='PROFILE', help='подписать профилем сертификата Tizen CLI')
ap.add_argument('--probe', action='store_true', help='проверочный пакет из probe/')
ap.add_argument('--ffprobe', action='store_true', help='отдельный пакет службы ffprobe')
ap.add_argument('--lib', metavar='SO', help='Tizen 5.5: libtsprobe.so вместо ffprobe (с --ffprobe)')
args = ap.parse_args()
if args.lib:
    args.ffprobe = True
hybrid = args.ffprobe
if hybrid and (args.sign or args.probe):
    raise SystemExit('--ffprobe не сочетается с --sign и --probe')

work = tempfile.mkdtemp()
app = os.path.join(work, 'app')
shutil.copytree(os.path.join(ROOT, 'ffprobe-app' if hybrid else ('probe' if args.probe else 'app')), app)
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
package = re.search(r'<tizen:application\b[^>]*\bpackage="([^"]+)"', cfg).group(1)

os.makedirs(args.out, exist_ok=True)
if hybrid:
    base = 'TorrStream-ffprobe-5.5' if args.lib else 'TorrStream-ffprobe'
else:
    base = 'TorrStream-Tizen-Probe' if args.probe else 'TorrStream-Tizen'
name = '%s_%s.wgt' % (base, version)
target = os.path.join(args.out, name)


def add_dir(z, folder, prefix):
    for cur, _, files in os.walk(folder):
        for f in sorted(files):
            full = os.path.join(cur, f)
            z.write(full, prefix + os.path.relpath(full, folder).replace(os.sep, '/'))


if args.sign:
    tizen = shutil.which('tizen') or shutil.which('tizen.bat')
    if not tizen:
        raise SystemExit('tizen CLI не найден в PATH (Tizen Studio: tools/ide/bin)')
    subprocess.check_call([tizen, 'package', '-t', 'wgt', '-s', args.sign, '--', app])
    built = [f for f in os.listdir(app) if f.endswith('.wgt')]
    shutil.move(os.path.join(app, built[0]), target)
elif not hybrid:
    # wgt — обычный zip; config.xml в корне
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        add_dir(z, app, '')
else:
    import build_ffprobe
    tpk = zipfile.ZipFile(build_ffprobe.build_tpk(version))
    # Служба — в пакете веб-приложения: тот же package, что в config.xml
    manifest = tpk.read('tizen-manifest.xml').decode('utf-8')
    manifest = re.sub(r'(<manifest\b[^>]*\bpackage=")[^"]*(")', r'\g<1>' + package + r'\g<2>', manifest, count=1)
    # Как у no-cover: в корневом config.xml — ещё и привилегии службы (exec.bin,
    # process.start…): права пакета установщик, судя по их пакету, берёт оттуда
    extra = ''
    for priv in re.findall(r'<privilege>([^<]+)</privilege>', manifest):
        if ('name="%s"' % priv) not in cfg:
            extra += '    <tizen:privilege name="%s"/>\n' % priv
    root_cfg = cfg.replace('</widget>', extra + '</widget>') if extra else cfg
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('config.xml', root_cfg)
        z.writestr('tizen-manifest.xml', manifest)
        for info in tpk.infolist():
            n = info.filename
            # Подписи Tizen.NET.Sdk — не наши: подпишет установщик весь wgt
            if n.endswith('/') or n in ('tizen-manifest.xml', 'author-signature.xml') or re.match(r'signature\d+\.xml$', n):
                continue
            # Сборка для 5.5: ffprobe-программа там не запустится — вместо неё библиотека
            if args.lib and n == 'res/ffprobe':
                continue
            z.writestr(info, tpk.read(n))
        add_dir(z, app, 'res/wgt/')
        if args.lib:
            # И в res/: каталог lib/ гибридного wgt установщик на Tizen 5.5, похоже,
            # не раскладывает (служба его не нашла), а res/ — раскладывает
            z.write(args.lib, 'res/libtsprobe.so')
            z.write(args.lib, 'lib/libtsprobe.so')

shutil.copyfile(target, os.path.join(args.out, base + '.wgt'))
shutil.rmtree(work, ignore_errors=True)
print('==', target, ('(гибридный: страница + служба, FFmpeg библиотекой)' if args.lib else '(гибридный: страница + служба ffprobe)') if hybrid else ('(подписан)' if args.sign else '(без подписи)'))
