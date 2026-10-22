# TorrStream для Samsung Tizen

Приложение TorrStream для телевизоров Samsung на Tizen — `.wgt`.

Как и приложения для webOS и Android, оно открывает интерфейс с сервера
TorrStream (по умолчанию общий `http://torrstream.online`, адрес можно
сменить), поэтому обновляется без переустановки. Отличие от браузера — только в
плеере: в приложении видео играет встроенный плеер TorrStream через `<video>`
прямо с TorrServer (прямой режим), без ffmpeg и серверного HLS.

Во фронтенде (`public/js` TorrStream) за это отвечают:
- `config.js: detectTizenApp` — приложение узнаётся по `window.tizen` (Device API)
  или по метке `?tsapp=tizen`, которую добавляет запускалка (метка живёт в
  `sessionStorage` до выхода). Браузер телевизора считается обычным браузером;
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

## Что в пакете

`app/`:
- `config.xml` — id `TorrStrm01.TorrStream`; `allow-navigation *` (сервер можно
  сменить); `hostedapp_deviceapi_allow` — Device API в странице с сервера
  (привилегия уровня partner);
- `index.html` — запускалка: через 3 секунды открывает сохранённый адрес, ОК во
  время отсчёта — форма смены адреса. Перед переходом проверяет `/api/version`.
  Регистрирует медиаклавиши пульта.

## Сборка

Тег `v*` → workflow `build.yml`: версия из тега → `tools/build.py` → релиз с
`TorrStream-Tizen.wgt` (постоянное имя:
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
