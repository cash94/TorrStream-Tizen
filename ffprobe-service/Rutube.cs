// Трейлеры RuTube с телевизора, мимо сервера TorrStream: GET /rutube?url=…
// (ProbeServer.cs).
//
// API RuTube (поиск, play/options) не отдаёт Access-Control-Allow-Origin, и
// страница TorrStream сама его ответ не прочитает. Служба делает запрос за неё
// и отвечает с заголовком CORS (ProbeServer.Respond). Само видео — плейлисты и
// сегменты — телевизор берёт с RuTube напрямую через <video>: медиаплеер CORS
// не проверяет (фронтенд, catalog.js: rutubeVideoDirect).
//
// Только https://rutube.ru/api/… и только с самого телевизора (как /ffprobe):
// иначе служба стала бы открытым прокси для всей домашней сети.
using System;
using System.IO;
using System.Net;
using System.Text;

namespace TorrStreamFfprobe
{
    static class Rutube
    {
        const int TimeoutMs = 15000;
        const int MaxBytes = 4 * 1024 * 1024;
        const string UserAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0 TV Safari/537.36";

        /// <summary>
        /// Тело ответа API или null; тогда status — код для страницы, error — причина
        /// </summary>
        public static string Get(string url, out int status, out string error)
        {
            status = 200;
            error = null;
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                uri.Scheme != "https" || uri.Host != "rutube.ru" || !uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                status = 400;
                error = "нужен адрес https://rutube.ru/api/…";
                return null;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET";
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.UserAgent = UserAgent;
                request.Accept = "application/json";
                request.Referer = "https://rutube.ru/";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.AllowAutoRedirect = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                    return ReadBody(response);
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    // Ответ RuTube с ошибкой (404 — ролик удалили): код отдаём как есть
                    status = (int)response.StatusCode;
                    error = "RuTube: HTTP " + status;
                    response.Dispose();
                }
                else
                {
                    status = e.Status == WebExceptionStatus.Timeout ? 504 : 502;
                    error = "RuTube: " + e.Status;
                }
                return null;
            }
            catch (Exception e)
            {
                status = 502;
                error = "RuTube: " + e.Message;
                return null;
            }
        }

        static string ReadBody(HttpWebResponse response)
        {
            var code = (int)response.StatusCode;
            if (code < 200 || code >= 300) throw new WebException("HTTP " + code);
            using (var body = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[16384];
                int n;
                while ((n = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + n > MaxBytes) throw new IOException("ответ больше " + (MaxBytes / 1024 / 1024) + " МБ");
                    buffer.Write(chunk, 0, n);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }
}
