# TorrStream для Samsung Tizen

Приложение TorrStream для телевизоров Samsung на Tizen — `.wgt`.

Как и приложения для webOS и Android, оно берёт интерфейс с сервера
TorrStream (по умолчанию общий `http://torrstream.online`, адрес можно
сменить), поэтому обновляется без переустановки.

С 1.1.0 страница остаётся в пакете (`file://`): запускалка скачивает HTML с
сервера и пишет его в себя, добавив `<base href>` на сервер и
`window.TS_SERVER`. Причина — Chrome 94+ (Tizen 7+) не пускает страницу с
публичного http-адреса к TorrServer в домашней сети (Private Network Access),
а страницу из пакета пускает (проверено на Tizen 9 пакетом `probe/`). Во
фронтенде (`config.js`) от `TS_SERVER` зависят `SERVER_URL`, вход по токену
`Authorization: Bearer` вместо cookie и воркеры из Blob. Нужен фронтенд с этой
поддержкой — старый (до 22.10.2026) с 1.1.0 не работает. Отличие от браузера — только в
плеере: в приложении видео играет встроенный плеер TorrStream через `<video>`
прямо с TorrServer (прямой режим), без ffmpeg и серверного HLS.

Во фронтенде (`public/js` TorrStream) за это отвечают:
- `config.js: detectTizenApp` — приложение узнаётся по `window.tizen` (Device API
  у страницы из пакета есть всегда) или по метке `tsApp` в `sessionStorage`.
  Браузер телевизора считается обычным браузером;
- `config.js: getKeyMap` — на Tizen 10009 только «Назад», 19 только пауза;
  ▶‖ 10252, ⏮/⏭ 10232/10233; удержание «Назад» — выход
  (`tizen.application…exit()`);
- `app.js: setupCheckboxes` — прямой режим (`transcodingFullOnOff`), настройки
  серверного транскодирования скрыты, как на webOS;
- `torrents.js: TizenTorrServer` — «TorrServer на этом устройстве» (см. ниже);
- «Об устройстве» → строка «Tizen API»: есть ли Device API у страницы.

## TorrServer на телевизоре

Скачать и запустить TorrServer из приложения, как на webOS с root или в
Android-приложении, на Tizen нельзя: веб-приложению закрыты запуск программ и
установка пакетов. TorrServer ставится отдельной службой (`.tpk`), например
[no-cover/torr-streaming](https://github.com/no-cover/torr-streaming) —
служба `tv.torr.streaming.tizen` поднимает официальный `TorrServer-linux-arm7`
на порту 8090 при включении телевизора. Ставится так же, как TorrStream.

В приложении: Настройки → TorrServer → «Использовать TorrServer, установленный
на этом устройстве». Приложение проверяет `127.0.0.1:8090/echo`; если служба
стоит, но не отвечает — просит систему её запустить.

## Служба ffprobe (отдельный пакет TorrStream-ffprobe.wgt)

Названия аудиодорожек и субтитров (Full, Forced, SDH, «по умолчанию»,
«принудительные») хранятся в самом файле. AVPlay их не отдаёт, только язык и
кодек. Их читает ffprobe — а веб-приложению Tizen запускать программы нельзя.
Поэтому ffprobe живёт в .NET-службе `ffprobe-service/` (appid
`online.torrstream.ffprobe`). Она ставится отдельным пакетом
`TorrStream-ffprobe.wgt` (id `TorrFfprb1.Ffprobe`), чтобы сам TorrStream-Tizen.wgt
оставался лёгкой запускалкой без .NET. Пакет гибридный, ровно как TorrServer от
no-cover/torr-streaming (их `tv.torr.streaming.tizen.wgt`):

    config.xml, tizen-manifest.xml   — в корне; package службы = package веб-приложения
    bin/  shared/res/  res/ffprobe   — .NET-служба
    res/wgt/                         — веб-часть (ffprobe-app/): запуск службы и её состояние

Значок «TorrStream ffprobe» в списке приложений открывает страницу, которая
будит службу и показывает, работает ли ffprobe. TorrStream и служба связаны
только по HTTP (IP телевизора:8095), ставить их можно в любом порядке.

Подписей внутри нет — подписывает установщик. Привилегии только уровня public
(у no-cover их много уровня partner/platform — с установщиком на телефоне
такие не ставятся).

- стартует вместе с телевизором и висит в фоне, окна у неё нет; запускалка
  TorrStream ещё и пробует будить её при каждом старте (`tizen.application.launch`);
- статический `ffprobe` armhf (johnvansickle.com, тот же, что в TorrStream-webOS)
  при запуске копируется в каталог данных и получает права на запуск;
- HTTP на порту 8095 (`ProbeServer.cs`): `GET /ffprobe?url=<адрес>` — JSON
  ffprobe файла прямо с TorrServer, только с самого телевизора; `GET /version` —
  с любого устройства сети: `http://<IP телевизора>:8095/version` в браузере
  телефона покажет, жива ли служба и запускается ли ffprobe (версия и время
  запуска или ошибка; `"checking": true` — проверка ещё идёт).

Фронтенд (`player.js: directProbeStart`) на Tizen спрашивает её первой, потом
сервис CUB, потом сведения сервера TorrStream. «Об устройстве» → «ffprobe на ТВ» —
установлена ли служба и её версия ffprobe. Не запустилась служба — всё
работает, как раньше (CUB).

Документация Tizen пишет, что гибрид (web + .NET) на профиле TV официально не
поддерживается и собирается Tizen Studio CLI, но пакет no-cover на телевизорах
ставится и работает; `tools/build.py` собирает такой же zip сам.

### Tizen 5.5: FFmpeg библиотекой (TorrStream-ffprobe-5.5.wgt)

На Tizen 5.5 службе запрещено запускать программы: и ffprobe-файл, и его копия
в памяти (memfd) дают «Operation not permitted». Поэтому для 5.5 — свой пакет
службы: вместо `res/ffprobe` в нём `res/libtsprobe.so` — урезанный FFmpeg (только
разбор контейнеров и http) с обёрткой `ffprobe-service/native/tsprobe.c`. Служба
загружает её к себе (`LibProbe.cs`, dlopen) — это не запуск программы — и
отдаёт тот же JSON, что ffprobe. Сборка — `tools/build_tsprobe.sh` (Linux:
zig cc под arm-linux-gnueabi, glibc 2.17 — Tizen на ARM armel/softfp, библиотеку
с hard-float загрузчик не берёт), затем
`python tools/build.py --ffprobe --lib tools/.cache/libtsprobe.so`; в CI — шаг «wgt для
Tizen 5.5». «Об устройстве» → «ffprobe на ТВ» — «FFmpeg библиотекой».

## Что в пакете

`app/`:
- `config.xml` — id `TorrStrm01.TorrStream`; `allow-navigation *` (сервер можно
  сменить); все привилегии уровня public (`hostedapp_deviceapi_allow`
  уровня partner убрана в 1.1.1: с public-сертификатом установка падала, а
  странице в пакете Device API доступен и без неё);
- `index.html` — запускалка: через 3 секунды загружает интерфейс с сохранённого
  адреса, ОК во время отсчёта — форма смены адреса. Перед этим проверяет
  `/api/version`. Перезагрузка страницы фронтендом — сразу загрузка, без
  отсчёта. Регистрирует медиаклавиши пульта.

`ffprobe-app/` — веб-часть пакета службы ffprobe (id `TorrFfprb1.Ffprobe`).

`probe/` — проверочный пакет (`build.py --probe`, id `TorrProbe1.Probe`): что
может страница в пакете — TorrServer в сети, `<video>`, сервер с токеном,
воркеры.

## Сборка

`python tools/build.py` — TorrStream-Tizen.wgt (только запускалка).
`python tools/build.py --ffprobe` — TorrStream-ffprobe.wgt (`tools/build.py` зовёт
`tools/build_ffprobe.py`): нужен .NET SDK (Tizen.NET.Sdk скачается сам), ffprobe
кэшируется в `tools/.cache`. С `--lib libtsprobe.so` — TorrStream-ffprobe-5.5.wgt.


Тег `v*` → workflow `build.yml`: версия из тега → `tools/build.py` → релиз с
`TorrStream-Tizen.wgt`, `TorrStream-ffprobe.wgt` и `TorrStream-ffprobe-5.5.wgt` (постоянное имя:
`releases/latest/download/TorrStream-Tizen.wgt`).

Локально: `python tools/build.py` (без подписи) или
`python tools/build.py --sign <профиль>` (Tizen CLI в PATH).

Пакет из CI не подписан: сертификат Samsung привязан к DUID телевизора, так
что подписывает тот, кто ставит.

## Установка на телевизор

1. На ТВ: Apps → набрать на пульте `12345` → Developer Mode **On**, Host PC IP —
   адрес компьютера, перезагрузить телевизор.
2. **Apps2Samsung** ([Jellyfin2Samsung](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer)):
   выбрать «Custom WGT» → `TorrStream-Tizen.wgt`. Программа сама войдёт в
   учётную запись Samsung, выпустит сертификат под телевизор и подпишет пакет.
3. Или **Tizen Studio**: Certificate Manager → профиль Samsung (Partner, DUID
   телевизора), затем
   `tizen package -t wgt -s <профиль> -- app` и
   `sdb connect <IP>` → `tizen install -n TorrStream-Tizen.wgt -t <устройство>`.
Служба ffprobe — по желанию, так же: `TorrStream-ffprobe.wgt` (Tizen 6+) или
`TorrStream-ffprobe-5.5.wgt` (Tizen 5.5). Если раньше стоял TorrStream 1.1.3
со службой внутри — сначала переустановите TorrStream-Tizen.wgt, потом ставьте
пакет службы: иначе два пакета с одной службой `online.torrstream.ffprobe`.
