"""
Сборка wgt TorrStream для Samsung Tizen.

  python tools/build.py [--version 1.0.1] [--out dist] [--plain | --sign ПРОФИЛЬ | --probe]
  python tools/build.py --ffprobe [--lib libtsprobe.so] [--version 1.0.1]

Версию подставляет в config.xml (в CI — из тега). Результат —
<имя>_<версия>.wgt и копия с постоянным именем <имя>.wgt.

Без ключей — TorrStream-Tizen.wgt: запускалка из app/ и .NET-служба дорожек
online.torrstream.tracks (порт 8096) — разбор заголовка MKV/MP4 на C#
(ffprobe-service/HeaderProbe.cs), без ffprobe. Работает и на Tizen 5.5, где
своё нативное (ffprobe файлом или из памяти, своя .so) не исполняется.

--ffprobe — отдельный TorrStream-ffprobe.wgt (Tizen 6+, свой id TorrFfprb1):
веб-часть ffprobe-app/ (запуск службы, её состояние) и та же служба с ffprobe
(online.torrstream.ffprobe, порт 8095) — для контейнеров, которые разбор не
знает. Фронтенд спрашивает 8096, потом 8095, потом CUB.

Оба — гибриды, как TorrServer от no-cover/torr-streaming
(tools/build_ffprobe.py собирает .tpk службы). Раскладка такая же, как у них:
  config.xml, tizen-manifest.xml   — в корне (package службы = package веб-приложения)
  bin/  shared/res/  res/ffprobe   — .NET-служба (из её .tpk; ffprobe — только в --ffprobe)
  res/wgt/                         — веб-часть, её config.xml тоже здесь
Подписей внутри нет: пакет подписывает тот, кто ставит (Apps2Samsung, установщик
с телефона, Tizen Studio) своим сертификатом Samsung — он привязан к DUID
телевизора. Нужен .NET SDK (Tizen.NET.Sdk скачается сам).

--plain — TorrStream-Tizen.wgt без службы (только запускалка, без .NET).
--lib libtsprobe.so — опыт с FFmpeg-библиотекой (TorrStream-ffprobe-lib.wgt,
tools/build_tsprobe.sh): на Tizen 5.5 dlopen — «failed to map segment».
--sign — обычный wgt через Tizen CLI (`tizen package -t wgt -s ПРОФИЛЬ`), без службы.
--probe — проверочный пакет из probe/ (TorrStream-Tizen-Probe.wgt, свой id).
"""
import argparse, os, re, shutil, subprocess, sys, tempfile, zipfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

ap = argparse.ArgumentParser()
ap.add_argument('--version', help='версия пакета (по умолчанию из config.xml)')
ap.add_argument('--out', default=os.path.join(ROOT, 'dist'))
ap.add_argument('--sign', metavar='PROFILE', help='подписать профилем сертификата Tizen CLI')
ap.add_argument('--probe', action='store_true', help='проверочный пакет из probe/')
ap.add_argument('--plain', action='store_true', help='TorrStream без службы дорожек')
ap.add_argument('--ffprobe', action='store_true', help='отдельный пакет службы ffprobe (Tizen 6+)')
ap.add_argument('--lib', metavar='SO', help='libtsprobe.so вместо ffprobe (с --ffprobe)')
args = ap.parse_args()
if args.lib:
    args.ffprobe = True
if args.ffprobe and (args.sign or args.probe or args.plain):
    raise SystemExit('--ffprobe не сочетается с --plain, --sign и --probe')
hybrid = not (args.plain or args.sign or args.probe)

work = tempfile.mkdtemp()
app = os.path.join(work, 'app')
shutil.copytree(os.path.join(ROOT, 'ffprobe-app' if args.ffprobe else ('probe' if args.probe else 'app')), app)
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


def check_xml(text, what):
    # Установщик Tizen с битым XML (хотя бы «--» в комментарии) отвечает только
    # «Invalid App. Could not read xml entry» — ловим это при сборке
    try:
        ET.fromstring(text.encode('utf-8'))
    except ET.ParseError as e:
        raise SystemExit('%s: неверный XML — %s' % (what, e))


check_xml(cfg, 'config.xml')
version =re.search(r'<widget\b[^>]*\bversion="([^"]+)"', cfg).group(1)
package = re.search(r'<tizen:application\b[^>]*\bpackage="([^"]+)"', cfg).group(1)

os.makedirs(args.out, exist_ok=True)
if args.ffprobe:
    base = 'TorrStream-ffprobe-lib' if args.lib else 'TorrStream-ffprobe'
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
    tpk = zipfile.ZipFile(build_ffprobe.build_tpk(version, with_ffprobe=args.ffprobe and not args.lib))
    # Служба — в пакете веб-приложения: тот же package, что в config.xml
    manifest = tpk.read('tizen-manifest.xml').decode('utf-8')
    manifest = re.sub(r'(<manifest\b[^>]*\bpackage=")[^"]*(")', r'\g<1>' + package + r'\g<2>', manifest, count=1)
    if not args.ffprobe:
        # Внутри TorrStream — служба дорожек: свой appid, по нему Program.cs
        # выбирает порт 8096 и разбор без ffprobe (с TorrStream-ffprobe.wgt
        # они стоят рядом)
        manifest = manifest.replace('appid="online.torrstream.ffprobe"', 'appid="online.torrstream.tracks"')
        manifest = re.sub(r'<label>[^<]*</label>', '<label>TorrStream tracks</label>', manifest, count=1)
        if 'online.torrstream.tracks' not in manifest:
            raise SystemExit('tizen-manifest.xml: не нашёл appid службы')
    # Как у no-cover: в корневом config.xml — ещё и привилегии службы (exec.bin,
    # process.start…): права пакета установщик, судя по их пакету, берёт оттуда
    extra = ''
    for priv in re.findall(r'<privilege>([^<]+)</privilege>', manifest):
        if ('name="%s"' % priv) not in cfg:
            extra += '    <tizen:privilege name="%s"/>\n' % priv
    root_cfg = cfg.replace('</widget>', extra + '</widget>') if extra else cfg
    check_xml(root_cfg, 'config.xml (корень)')
    check_xml(manifest, 'tizen-manifest.xml')
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('config.xml', root_cfg)
        z.writestr('tizen-manifest.xml', manifest)
        for info in tpk.infolist():
            n = info.filename
            # Подписи Tizen.NET.Sdk — не наши: подпишет установщик весь wgt
            if n.endswith('/') or n in ('tizen-manifest.xml', 'author-signature.xml') or re.match(r'signature\d+\.xml$', n):
                continue
            # Опыт с библиотекой: вместо ffprobe — libtsprobe.so
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
print('==', target, ('(гибридный: страница + служба, FFmpeg библиотекой)' if args.lib else '(гибридный: страница + служба ffprobe)' if args.ffprobe else '(гибридный: запускалка + служба дорожек MKV/MP4)') if hybrid else ('(подписан)' if args.sign else '(без подписи)'))
