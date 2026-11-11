/*
 * Служба Smart Hub Preview TorrStream (веб-служба Tizen, среда как у Node.js).
 *
 * Карточки над ярлыком приложения в Smart Hub — аналог каналов Android TV.
 * Ряды собирает фронтенд (js/tizen-preview.js во фронтенде): у него уже есть
 * адрес сервера, история, раздачи TorrServer и подборки главной. Готовый JSON
 * он передаёт сюда в данных запуска (ключ «preview», строка кусками), а служба
 * отдаёт его системе через webapis.preview.setPreviewData — из самой страницы
 * приложения Samsung этот вызов не документирует.
 *
 * Последний JSON служба кладёт в файл: система будит её и без приложения (раз
 * в ~10 минут, после включения телевизора) — тогда повторяем сохранённые
 * карточки, если система их забыла. Сохранить не вышло — просто выходим.
 *
 * Правила Samsung: после setPreviewData службу закрыть самой (exit), иначе
 * через 5 минут её снимет система и в следующий раз не запустит.
 */
var fs = null;
try { fs = require('fs'); } catch (e) { }

var FILE_NAME = 'smarthub-preview.json';
// Ответ приложению (js/tizen-preview.js во фронтенде): что сказал Smart Hub.
// Приложение не запущено (будила система) — порта нет, и ладно
var APP_ID = 'TorrStrm01.TorrStream';
var STATUS_PORT = 'TS_PREVIEW_STATUS';
var exited = false;
var fromApp = false;

function log(msg) {
    try { console.log('[TorrStream preview] ' + msg); } catch (e) { }
}

function reply(result, message) {
    if (!fromApp) return;
    try {
        var port = tizen.messageport.requestRemoteMessagePort(APP_ID, STATUS_PORT);
        var data = [{ key: 'result', value: result }];
        if (message) data.push({ key: 'message', value: String(message) });
        port.sendMessage(data);
    } catch (e) {
        log('ответ приложению: ' + e.message);
    }
}

function exit() {
    if (exited) return;
    exited = true;
    try { tizen.application.getCurrentApplication().exit(); } catch (e) { }
}

/** Каталог data пакета: Tizen 3+ — apps_rw, Tizen 2.4 (ТВ 2016) — /opt/usr/apps */
function dataDir() {
    if (!fs) return null;
    var pkg = 'TorrStrm01';
    try { pkg = tizen.application.getCurrentApplication().appInfo.packageId || pkg; } catch (e) { }
    var dirs = ['/opt/usr/home/owner/apps_rw/' + pkg + '/data', '/opt/usr/apps/' + pkg + '/data'];
    for (var i = 0; i < dirs.length; i++) {
        try { if (fs.existsSync(dirs[i])) return dirs[i]; } catch (e) { }
    }
    return null;
}

function save(json) {
    var dir = dataDir();
    if (!dir) return;
    try { fs.writeFileSync(dir + '/' + FILE_NAME, json, 'utf8'); } catch (e) { log('не сохранил: ' + e.message); }
}

function load() {
    var dir = dataDir();
    if (!dir) return null;
    try { return fs.readFileSync(dir + '/' + FILE_NAME, 'utf8'); } catch (e) { return null; }
}

/** JSON карточек из данных запуска: приложение кладёт его в «preview» кусками */
function requestedPreview() {
    try {
        var req = tizen.application.getCurrentApplication().getRequestedAppControl();
        var data = (req && req.appControl && req.appControl.data) || [];
        for (var i = 0; i < data.length; i++) {
            if (data[i].key === 'preview' && data[i].value && data[i].value.length) return data[i].value.join('');
        }
    } catch (e) {
        log('appControl: ' + e.message);
    }
    return null;
}

function publish(json) {
    var fail = function (msg) {
        log(msg);
        reply('error', msg);
        exit();
    };
    try {
        JSON.parse(json);
    } catch (e) {
        return fail('битый JSON карточек');
    }
    if (typeof webapis === 'undefined' || !webapis.preview || typeof webapis.preview.setPreviewData !== 'function') {
        return fail('в службе нет webapis.preview');
    }
    try {
        webapis.preview.setPreviewData(json, function () {
            log('карточки переданы');
            reply('ok');
            exit();
        }, function (e) {
            fail('setPreviewData: ' + ((e && (e.message || e.name)) || 'ошибка'));
        });
    } catch (e) {
        fail('setPreviewData: ' + e.message);
    }
}

module.exports.onStart = function () {
    // Всё делает onRequest: он приходит и на запуск системой, и на запуск
    // приложением. На случай, если onRequest не придёт, — страховка
    setTimeout(exit, 60 * 1000);
};

module.exports.onRequest = function () {
    var json = requestedPreview();
    fromApp = !!json;
    if (json) {
        reply('started');
        save(json);
    }
    else json = load();
    if (!json) return exit();
    publish(json);
};

module.exports.onExit = function () { };
