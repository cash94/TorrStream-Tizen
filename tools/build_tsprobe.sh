#!/usr/bin/env bash
# Сборка ffprobe-service/native → libtsprobe.so для Tizen 5.5 (armel).
#
# Урезанный FFmpeg (только разбор контейнеров + http/tcp, без декодеров,
# без asm и внешних библиотек) статически внутри одной libtsprobe.so с
# двумя функциями: tsprobe_version, tsprobe_probe (native/tsprobe.c).
# Компилятор — zig cc: собирает под arm-linux-gnueabi сразу для старой
# glibc (2.17), без sysroot — такая .so загрузится и на Tizen 5.5.
# Именно gnueabi, не gnueabihf: Tizen на ARM — armel (softfp, и .NET там
# linux-armel), библиотеку с hard-float загрузчик процесса службы не берёт.
# Статическому ffprobe это было всё равно, а .so грузится в чужой процесс.
#
#   bash tools/build_tsprobe.sh [каталог-результата]   (Linux; в CI — build.yml)
#
# Результат: <каталог>/libtsprobe.so (по умолчанию tools/.cache/libtsprobe.so).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/tools/.cache}"
WORK="$(mktemp -d)"
ZIG_VER=0.13.0
FFMPEG_VER=7.0.2
TARGET=arm-linux-gnueabi.2.17
CPU=cortex_a9

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"   # абсолютный: дальше скрипт уходит в $WORK
cd "$WORK"

echo "== zig $ZIG_VER"
curl -sSfL "https://ziglang.org/download/$ZIG_VER/zig-linux-x86_64-$ZIG_VER.tar.xz" | tar -xJ
ZIG="$WORK/zig-linux-x86_64-$ZIG_VER/zig"

# Обёртки: configure FFmpeg ждёт отдельные программы, а не «zig cc …»
mkdir -p bin
cat > bin/cc <<EOF
#!/bin/sh
exec "$ZIG" cc -target $TARGET -mcpu=$CPU "\$@"
EOF
cat > bin/ar <<EOF
#!/bin/sh
exec "$ZIG" ar "\$@"
EOF
cat > bin/ranlib <<EOF
#!/bin/sh
exec "$ZIG" ranlib "\$@"
EOF
chmod +x bin/*

echo "== FFmpeg $FFMPEG_VER"
curl -sSfL "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VER.tar.xz" | tar -xJ
cd "ffmpeg-$FFMPEG_VER"

./configure \
  --enable-cross-compile --arch=arm --target-os=linux \
  --cc="$WORK/bin/cc" --ar="$WORK/bin/ar" --ranlib="$WORK/bin/ranlib" \
  --nm=nm --strip=true --host-cc=gcc \
  --enable-pic --enable-static --disable-shared \
  --disable-autodetect --disable-asm --disable-debug --disable-doc --disable-programs \
  --disable-avdevice --disable-swscale --disable-swresample --disable-postproc --disable-avfilter \
  --disable-everything \
  --enable-network --enable-protocol=http,tcp,file \
  --enable-demuxer=matroska,mov,avi,mpegts,mpegps,flv,asf,ogg,wav,mp3,aac,ac3,eac3,dts,flac,h264,hevc \
  --enable-small

# configure находит sysctl, а в заголовках glibc из zig (новых) sys/sysctl.h уже
# нет — libavutil/cpu.c не собирался. На Linux FFmpeg он не нужен
sed -i 's/#define HAVE_SYSCTL 1/#define HAVE_SYSCTL 0/' config.h
grep -n 'HAVE_SYSCTL' config.h || true

make -j"$(nproc)"

echo "== libtsprobe.so"
cat > exports.map <<'EOF'
{ global: tsprobe_version; tsprobe_probe; local: *; };
EOF
"$WORK/bin/cc" -shared -fPIC -O2 -fvisibility=hidden \
  -I. -o "$OUT/libtsprobe.so" "$ROOT/ffprobe-service/native/tsprobe.c" \
  libavformat/libavformat.a libavcodec/libavcodec.a libavutil/libavutil.a \
  -Wl,--version-script=exports.map -lm -lpthread

# Проверка: какие версии glibc и символы нужны библиотеке
echo "== зависимости"
readelf -h "$OUT/libtsprobe.so" | grep Flags   # нужен soft-float ABI, не hard-float
readelf -d "$OUT/libtsprobe.so" | grep NEEDED || true
readelf -V "$OUT/libtsprobe.so" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -3
readelf --dyn-syms "$OUT/libtsprobe.so" | grep -E 'tsprobe_(version|probe)'
ls -la "$OUT/libtsprobe.so"
rm -rf "$WORK"
