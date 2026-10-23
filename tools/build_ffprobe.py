"""
Служба дорожек для Samsung Tizen (ffprobe-service/) — .NET-часть
TorrStream-Tizen.wgt (разбор MKV/MP4) и TorrStream-ffprobe.wgt (ffprobe).
Обычно её собирает tools/build.py внутрь wgt гибридом, как
no-cover/torr-streaming; отдельно, для проверки:

  python tools/build_ffprobe.py [--version 1.1.3] [--out dist]   → TorrStream-ffprobe.tpk

Нужен .NET SDK (6+); Tizen.NET.Sdk dotnet скачает сам. ffprobe — статическая
сборка armhf с johnvansickle.com (та же, что в TorrStream-webOS), кэш —
tools/.cache/ffprobe.
"""
import argparse, io, lzma, os, re, shutil, subprocess, tarfile, tempfile, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'ffprobe-service')
CACHE = os.path.join(ROOT, 'tools', '.cache')
FFPROBE_URL = 'https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-armhf-static.tar.xz'


# Сайт иногда отдаёт CI вместо архива что-то другое (2026-10-23 сборка тега
# v1.1.4 упала на «Input format not supported by decoder», повтор прошёл), —
# поэтому несколько попыток с паузами и проверка того, что скачалось
RETRY_DELAYS = (0, 10, 30, 60)
XZ_MAGIC = b'\xfd7zXZ\x00'
ELF_MAGIC = b'\x7fELF'


def _download_ffprobe():
    """Одна попытка: архив → байты ffprobe; что не так — исключение с причиной"""
    req = urllib.request.Request(FFPROBE_URL, headers={'User-Agent': 'TorrStream-Tizen build'})
    with urllib.request.urlopen(req, timeout=300) as r:
        data = r.read()
        ctype = r.headers.get('Content-Type', '')
    if not data.startswith(XZ_MAGIC):
        raise ValueError('не xz-архив (%d байт, %s): %r' % (len(data), ctype or '?', data[:60]))
    with tarfile.open(fileobj=io.BytesIO(lzma.decompress(data))) as tar:
        member = next((m for m in tar.getmembers() if m.name.endswith('/ffprobe')), None)
        if member is None:
            raise ValueError('в архиве нет ffprobe')
        binary = tar.extractfile(member).read()
    if not binary.startswith(ELF_MAGIC):
        raise ValueError('ffprobe в архиве — не ELF')
    return binary


def ffprobe_binary():
    """Статический ffprobe под armhf (32-битное пространство ТВ); кэш — tools/.cache"""
    path = os.path.join(CACHE, 'ffprobe')
    if os.path.isfile(path):
        with open(path, 'rb') as f:
            if f.read(4) == ELF_MAGIC:
                return path
        os.remove(path)   # битый кэш — качаем заново
    os.makedirs(CACHE, exist_ok=True)
    errors = []
    for attempt, delay in enumerate(RETRY_DELAYS, 1):
        if delay:
            print('== повтор через %d с' % delay)
            time.sleep(delay)
        print('== скачиваю (%d/%d) %s' % (attempt, len(RETRY_DELAYS), FFPROBE_URL))
        try:
            binary = _download_ffprobe()
        except Exception as e:
            print('== не вышло:', e)
            errors.append(str(e))
            continue
        tmp = path + '.part'
        with open(tmp, 'wb') as f:
            f.write(binary)
        os.replace(tmp, path)
        return path
    raise SystemExit('ffprobe не скачался за %d попыток: %s' % (len(RETRY_DELAYS), errors[-1]))


def build_tpk(version=None, with_ffprobe=True):
    """Собрать .tpk службы во временном каталоге; путь к нему (каталог — забота вызывающего).
    with_ffprobe=False — без res/ffprobe (служба внутри TorrStream-Tizen.wgt: только разбор)"""
    work = tempfile.mkdtemp()
    proj = os.path.join(work, 'ffprobe-service')
    shutil.copytree(SRC, proj, ignore=shutil.ignore_patterns('bin', 'obj', 'ffprobe'))
    os.makedirs(os.path.join(proj, 'res'), exist_ok=True)
    if with_ffprobe:
        shutil.copyfile(ffprobe_binary(), os.path.join(proj, 'res', 'ffprobe'))

    if version:
        if not re.match(r'^\d+\.\d+\.\d+$', version):
            raise SystemExit('версия должна быть вида 1.0.1: ' + version)
        man_path = os.path.join(proj, 'tizen-manifest.xml')
        man = open(man_path, encoding='utf-8').read()
        man = re.sub(r'(<manifest\b[^>]*\bversion=")[^"]*(")', r'\g<1>' + version + r'\g<2>', man, count=1)
        open(man_path, 'w', encoding='utf-8').write(man)

    subprocess.check_call(['dotnet', 'build', '-c', 'Release', '-nologo', '-v', 'q'], cwd=proj)
    for folder, _, files in os.walk(os.path.join(proj, 'bin')):
        for f in files:
            if f.endswith('.tpk'):
                return os.path.join(folder, f)
    raise SystemExit('tpk не собрался')


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', help='версия пакета (по умолчанию из tizen-manifest.xml)')
    ap.add_argument('--out', default=os.path.join(ROOT, 'dist'))
    args = ap.parse_args()
    tpk = build_tpk(args.version)
    os.makedirs(args.out, exist_ok=True)
    target = os.path.join(args.out, 'TorrStream-ffprobe.tpk')
    shutil.copyfile(tpk, target)
    print('==', target)
