/*
 * libtsprobe.so — сведения о файле через libavformat, без запуска программ.
 *
 * Tizen 5.5 не даёт службе запускать программы («Operation not permitted» —
 * и для файла, и для копии в памяти), поэтому ffprobe там не работает. Эта
 * библиотека — урезанный FFmpeg (только разбор контейнеров и http) внутри
 * процесса .NET-службы (ProbeServer.cs, LibProbe.cs): загрузка библиотеки —
 * не запуск программы.
 *
 * Ответ — JSON в том же виде, что у «ffprobe -print_format json -show_streams
 * -show_format» (поля, которые читает фронтенд: player.js: applyDirectProbe):
 *   {"streams":[{"index":0,"codec_type":"audio","codec_name":"ac3","channels":6,
 *     "tags":{"language":"rus","title":"Дубляж"},"disposition":{"default":1,"forced":0}}],
 *    "format":{"format_name":"matroska,webm","duration":"5400.000000"}}
 * Декодеров в сборке нет: avformat_find_stream_info не зовём — всё нужное
 * (язык, название, кодек, каналы) лежит в заголовке контейнера.
 *
 * Сборка — tools/build_tsprobe.sh (zig cc под arm-linux-gnueabihf, glibc 2.17).
 */
#include <stdio.h>
#include <string.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libavutil/dict.h>

#define EXPORT __attribute__((visibility("default")))

typedef struct { char *p; int left; int len; } Out;

static void put(Out *o, const char *s)
{
    int n = (int)strlen(s);
    o->len += n;
    if (n >= o->left) n = o->left > 0 ? o->left - 1 : 0;
    if (n > 0) { memcpy(o->p, s, n); o->p += n; o->left -= n; }
    if (o->left > 0) *o->p = 0;
}

static void put_str(Out *o, const char *s)
{
    char esc[8];
    put(o, "\"");
    for (; s && *s; s++) {
        unsigned char c = (unsigned char)*s;
        if (c == '"' || c == '\\') { esc[0] = '\\'; esc[1] = (char)c; esc[2] = 0; put(o, esc); }
        else if (c < 0x20) { snprintf(esc, sizeof esc, "\\u%04x", c); put(o, esc); }
        else { esc[0] = (char)c; esc[1] = 0; put(o, esc); }
    }
    put(o, "\"");
}

static void put_int(Out *o, long long v)
{
    char b[32];
    snprintf(b, sizeof b, "%lld", v);
    put(o, b);
}

/* Версия для /version службы */
EXPORT int tsprobe_version(char *buf, int size)
{
    snprintf(buf, size, "%s (FFmpeg %s)", LIBAVFORMAT_IDENT, av_version_info());
    return 0;
}

/*
 * Сведения о файле по адресу url (http://… TorrServer). В buf — JSON или текст
 * ошибки. Возврат: 0 — JSON, < 0 — ошибка (текст в buf), > 0 — buf мал (нужно
 * столько байт). timeout_ms — на чтение по сети (rw_timeout).
 */
EXPORT int tsprobe_probe(const char *url, char *buf, int size, int timeout_ms)
{
    AVFormatContext *fmt = NULL;
    AVDictionary *opts = NULL;
    char tmp[64];
    unsigned i;
    int err;
    Out o;

    avformat_network_init();
    snprintf(tmp, sizeof tmp, "%lld", (long long)timeout_ms * 1000);
    av_dict_set(&opts, "rw_timeout", tmp, 0);
    err = avformat_open_input(&fmt, url, NULL, &opts);
    av_dict_free(&opts);
    if (err < 0) {
        char e[128];
        av_strerror(err, e, sizeof e);
        snprintf(buf, size, "avformat_open_input: %s", e);
        return -1;
    }

    o.p = buf; o.left = size; o.len = 0;
    if (size > 0) buf[0] = 0;
    put(&o, "{\"streams\":[");
    for (i = 0; i < fmt->nb_streams; i++) {
        AVStream *st = fmt->streams[i];
        AVCodecParameters *par = st->codecpar;
        const char *type = av_get_media_type_string(par->codec_type);
        AVDictionaryEntry *lang = av_dict_get(st->metadata, "language", NULL, 0);
        AVDictionaryEntry *title = av_dict_get(st->metadata, "title", NULL, 0);
        if (i) put(&o, ",");
        put(&o, "{\"index\":"); put_int(&o, st->index);
        put(&o, ",\"codec_type\":"); put_str(&o, type ? type : "unknown");
        put(&o, ",\"codec_name\":"); put_str(&o, avcodec_get_name(par->codec_id));
        if (par->codec_type == AVMEDIA_TYPE_AUDIO) {
            put(&o, ",\"channels\":"); put_int(&o, par->ch_layout.nb_channels);
            put(&o, ",\"sample_rate\":\""); put_int(&o, par->sample_rate); put(&o, "\"");
        }
        if (par->codec_type == AVMEDIA_TYPE_VIDEO) {
            put(&o, ",\"width\":"); put_int(&o, par->width);
            put(&o, ",\"height\":"); put_int(&o, par->height);
        }
        put(&o, ",\"tags\":{");
        if (lang) { put(&o, "\"language\":"); put_str(&o, lang->value); }
        if (title) { if (lang) put(&o, ","); put(&o, "\"title\":"); put_str(&o, title->value); }
        put(&o, "},\"disposition\":{\"default\":");
        put_int(&o, (st->disposition & AV_DISPOSITION_DEFAULT) ? 1 : 0);
        put(&o, ",\"forced\":");
        put_int(&o, (st->disposition & AV_DISPOSITION_FORCED) ? 1 : 0);
        put(&o, "}}");
    }
    put(&o, "],\"format\":{\"format_name\":");
    put_str(&o, fmt->iformat ? fmt->iformat->name : "");
    if (fmt->duration > 0) {
        snprintf(tmp, sizeof tmp, "%.6f", fmt->duration / (double)AV_TIME_BASE);
        put(&o, ",\"duration\":"); put_str(&o, tmp);
    }
    put(&o, "}}");
    avformat_close_input(&fmt);
    return o.len < size ? 0 : o.len + 1;
}
