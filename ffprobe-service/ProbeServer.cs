// HTTP-сервер службы ffprobe (Program.cs — обвязка Tizen). Без зависимостей от
// Tizen: его же можно запустить на ПК обычной консольной программой.
//
//   GET /version             — {"name":"TorrStream ffprobe","service":"1.0.0","ffprobe":"<строка версии>"}
//   GET /ffprobe?url=<адрес> — JSON ffprobe (-show_format -show_streams) файла
//                              по этому адресу (TorrServer /stream…)
// Ответы с Access-Control-Allow-Origin: * — страница TorrStream лежит в
// пакете приложения (file://).
//
// Слушаем все адреса телевизора, но с других устройств сети пускаем только к
// /version: открыть http://<IP телевизора>:8095/version в браузере телефона —
// и видно, жива ли служба и запускается ли ffprobe. /ffprobe — только с самого
// телевизора: иначе любой в сети мог бы заставить его открывать чужие адреса.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TorrStreamFfprobe
{
    public class ProbeServer
    {
        public const string ServiceVersion = "1.0.0";
        const int ProbeTimeoutMs = 60000;

        readonly int port;
        readonly string ffprobePath;   // null — ffprobe не готов (stageError)
        readonly string stageError;
        readonly Action<string> log;
        // Запуск из памяти (Program.cs: memfd → /proc/self/fd/N). Tizen 5.5 не даёт
        // запустить файл из каталога приложения («Operation not permitted»), а
        // копию в памяти — даёт: так обходит это и TorrServer от no-cover.
        // Пробуем, только если запуск файла не удался
        readonly Func<string> memoryPathFactory;
        readonly object execLock = new object();
        string execPath;               // чем запускаем сейчас
        string execMethod = "файл";
        string fileExecError;          // почему не удалось запустить файл
        string memoryError;            // почему не удалось из памяти
        bool memoryTried;
        TcpListener listener;
        // Итог проверки «ffprobe -version»: оба null — ещё идёт. Проверка в фоне
        // при старте: на Tizen 9 запуск ffprobe бывает долгим, и /version, ждавший
        // его, не успевал ответить странице («ffprobe на ТВ: не отвечает»)
        volatile string ffprobeVersion;
        volatile string ffprobeVersionError;

        public ProbeServer(int port, string ffprobePath, string stageError, Action<string> log)
            : this(port, ffprobePath, stageError, log, null) { }

        public ProbeServer(int port, string ffprobePath, string stageError, Action<string> log, Func<string> memoryPathFactory)
        {
            this.port = port;
            this.ffprobePath = ffprobePath;
            this.execPath = ffprobePath;
            this.stageError = stageError;
            this.log = log ?? (_ => { });
            this.memoryPathFactory = memoryPathFactory;
        }

        // Tizen 5.5: FFmpeg библиотекой внутри процесса (LibProbe.cs, libtsprobe.so)
        // вместо запуска ffprobe — программы там службе запускать нельзя
        readonly Func<string, string> libProbe;   // url → JSON, ошибка — исключение
        readonly string libVersion;

        public ProbeServer(int port, Func<string, string> libProbe, string libVersion, string stageError, Action<string> log)
            : this(port, null, stageError, log, null)
        {
            this.libProbe = libProbe;
            this.libVersion = libVersion;
            if (libProbe != null) execMethod = "библиотека";
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            var thread = new Thread(AcceptLoop) { IsBackground = true };
            thread.Start();
            log("слушаю порт " + port + " (из сети — только /version)");
            if (ffprobePath != null || libProbe != null)
            {
                var check = new Thread(CheckVersion) { IsBackground = true };
                check.Start();
            }
        }

        public void Stop()
        {
            try { if (listener != null) listener.Stop(); } catch { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; } // listener остановлен
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var stream = client.GetStream();
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    // «Свой» — с самого телевизора: 127.x или его же адрес в сети.
                    // Страница TorrStream на ТВ ходит сюда по IP телевизора: до
                    // localhost она там не достаёт (как и до TorrServer)
                    bool fromSelf = remote != null && (IPAddress.IsLoopback(remote.Address) || IsOwnAddress(remote.Address));
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(requestLine)) return;
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) { } // заголовки не нужны

                    string[] parts = requestLine.Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "";
                    string target = parts.Length > 1 ? parts[1] : "/";

                    if (method == "OPTIONS") { Respond(stream, 204, "No Content", ""); return; }
                    if (method != "GET") { Respond(stream, 405, "Method Not Allowed", Json("error", "только GET")); return; }

                    string path = target, query = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

                    if (path == "/version") { Respond(stream, 200, "OK", VersionJson()); return; }
                    if (!fromSelf) { Respond(stream, 403, "Forbidden", Json("error", "из сети доступен только /version")); return; }
                    if (path == "/ffprobe")
                    {
                        string error;
                        string json = Probe(QueryParam(query, "url"), out error);
                        if (json != null) Respond(stream, 200, "OK", json);
                        else Respond(stream, 500, "Error", Json("error", error));
                        return;
                    }
                    Respond(stream, 404, "Not Found", Json("error", "нет такого адреса"));
                }
                catch (Exception e)
                {
                    log("запрос: " + e.Message);
                }
            }
        }

        /// <summary>
        /// Итог проверки ffprobe одной строкой — для уведомления на экране
        /// (Program.cs, как «TORRSERVER STARTED» у no-cover)
        /// </summary>
        public Action<string> VersionChecked;

        void CheckVersion()
        {
            if (libProbe != null)
            {
                ffprobeVersion = libVersion;
                log("библиотека: " + libVersion);
                var n = VersionChecked;
                if (n != null) { try { n("TorrStream: FFmpeg-библиотека загружена — " + libVersion); } catch { } }
                return;
            }
            var started = DateTime.UtcNow;
            string err;
            string output = RunFfprobe("-version", 120000, out err);
            int ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (output != null) ffprobeVersion = output.Split('\n')[0].Trim() + " (" + ms + " мс)";
            else ffprobeVersionError = (err ?? "ошибка") + " (" + ms + " мс)";
            log("ffprobe -version: " + (ffprobeVersion ?? ffprobeVersionError));
            var notify = VersionChecked;
            if (notify != null)
            {
                // «ffprobe version 7.0.2-static https://…» → «7.0.2-static»
                string text;
                if (output != null)
                {
                    string[] words = output.Split('\n')[0].Trim().Split(' ');
                    string ver = words.Length > 2 && words[1] == "version" ? words[2] : "";
                    text = "TorrStream: ffprobe " + ver + " работает" + (execMethod == "память" ? " (запуск из памяти)" : "");
                }
                else text = "TorrStream: ffprobe не запускается — " + (err ?? "ошибка");
                try { notify(text); } catch { }
            }
        }

        // Отвечает сразу: версия ffprobe — из фоновой проверки (CheckVersion)
        string VersionJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":\"TorrStream ffprobe\",\"service\":\"").Append(ServiceVersion).Append("\",\"ffprobe\":");
            sb.Append(ffprobeVersion == null ? "null" : Quote(ffprobeVersion));
            sb.Append(",\"exec\":").Append(Quote(execMethod));
            // Разрядность процесса службы: 32-битная libtsprobe.so (сборка для
            // Tizen 5.5) в 64-битный процесс не загрузится
            sb.Append(",\"arch\":").Append(Quote(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()));
            string lanIp = LanAddress();
            if (lanIp != null) sb.Append(",\"lan\":").Append(Quote(lanIp));
            if (fileExecError != null) sb.Append(",\"fileError\":").Append(Quote(fileExecError));
            if (memoryError != null) sb.Append(",\"memoryError\":").Append(Quote(memoryError));
            if ((ffprobePath != null || libProbe != null) && ffprobeVersion == null && ffprobeVersionError == null) sb.Append(",\"checking\":true");
            string error = stageError ?? ffprobeVersionError;
            if (error != null) sb.Append(",\"error\":").Append(Quote(error));
            sb.Append("}");
            return sb.ToString();
        }

        // Адрес — только http(s) и без кавычек и пробелов: он уходит ffprobe одним
        // аргументом, без оболочки, но в общей строке аргументов
        string Probe(string url, out string error)
        {
            error = null;
            if (ffprobePath == null && libProbe == null) { error = stageError ?? "ffprobe не готов"; return null; }
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://")) ||
                url.IndexOf('"') >= 0 || url.IndexOf(' ') >= 0 || url.IndexOf('\\') >= 0)
            {
                error = "нужен адрес http(s)";
                return null;
            }
            // TorrServer на самом телевизоре (localhost/127.x): ffprobe из службы
            // до него, бывает, не достаёт, а по адресу телевизора в сети — да
            // (наблюдение на Tizen 9). Открывается за 2 с соединение с адресом
            // в сети на тот же порт — ffprobe идёт туда, иначе по исходному.
            // Проверка соединением, а не запуском ffprobe: тот до недоступного
            // адреса ждёт минуту
            if (LoopbackHost.IsMatch(url))
            {
                string lan = LanAddress();
                int tsPort = 0;
                try { tsPort = new Uri(url).Port; } catch { }
                if (lan != null && tsPort > 0 && CanConnect(lan, tsPort, 2000))
                    url = LoopbackHost.Replace(url, "${1}${2}" + lan, 1);
            }
            return ProbeOnce(url, out error);
        }

        static bool CanConnect(string host, int tcpPort, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, tcpPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        static readonly System.Text.RegularExpressions.Regex LoopbackHost = new System.Text.RegularExpressions.Regex(
            @"^(https?://)([^/@]*@)?(?:localhost|127\.\d+\.\d+\.\d+)(?=[:/]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>IPv4-адрес телевизора в домашней сети (первый работающий не-loopback) или null</summary>
        static bool IsOwnAddress(IPAddress addr)
        {
            try
            {
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.Equals(addr)) return true;
            }
            catch { }
            return false;
        }

        static string LanAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                            return a.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        // Ответ без «streams» (ffprobe с -v quiet при ошибке печатает «{}») — неудача
        string ProbeOnce(string url, out string error)
        {
            string output;
            error = null;
            if (libProbe != null)
            {
                try { output = libProbe(url); }
                catch (Exception e) { error = e.Message; return null; }
            }
            // -nofind_stream_info: только заголовок контейнера (названия, язык,
            // кодек, каналы — всё там). Без него ffprobe ещё декодирует начало
            // потоков, это мегабайты из раздачи: на Tizen 9 ответ шёл 35 с, и
            // CUB успевал раньше
            else output = RunFfprobe("-v quiet -print_format json -show_format -show_streams -nofind_stream_info \"" + url + "\"", ProbeTimeoutMs, out error);
            if (output == null) return null;
            if (output.TrimStart().StartsWith("{") && output.IndexOf("\"streams\"", StringComparison.Ordinal) >= 0) return output;
            if (error == null) error = "ffprobe не разобрал файл (TorrServer не отдал его?)";
            return null;
        }

        // Файл не запустился (исключение Process.Start, а не код выхода ffprobe) —
        // один раз пробуем копию в памяти и дальше запускаем её
        string RunFfprobe(string args, int timeoutMs, out string error)
        {
            string path;
            lock (execLock) path = execPath;
            bool startFailed;
            string output = RunAt(path, args, timeoutMs, out error, out startFailed);
            if (!startFailed || memoryPathFactory == null) return output;
            lock (execLock)
            {
                if (!memoryTried)
                {
                    memoryTried = true;
                    fileExecError = error;
                    try
                    {
                        execPath = memoryPathFactory();
                        execMethod = "память";
                        log("файл не запускается (" + error + ") — запуск из памяти: " + execPath);
                    }
                    catch (Exception e)
                    {
                        memoryError = e.Message;
                        log("из памяти тоже нельзя: " + e.Message);
                    }
                }
                if (execPath == path) return output;
                path = execPath;
            }
            output = RunAt(path, args, timeoutMs, out error, out startFailed);
            if (startFailed && memoryError == null) memoryError = error;
            return output;
        }

        string RunAt(string path, string args, int timeoutMs, out string error, out bool startFailed)
        {
            error = null;
            startFailed = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                Process started;
                try { started = Process.Start(psi); }
                catch (Exception e)
                {
                    // Не запустился вовсе (EPERM, нет файла) — RunFfprobe попробует из памяти
                    startFailed = true;
                    error = e.Message;
                    return null;
                }
                using (var p = started)
                {
                    // Читаем асинхронно: большой вывод забил бы канал и ffprobe встал бы.
                    // Конец работы — по закрытию вывода, а не по WaitForExit: среда
                    // приложений Tizen сама забирает завершённые дочерние процессы
                    // (SIGCHLD), и .NET об их выходе не узнаёт — на Tizen 9
                    // «ffprobe -version» «не отвечал» 120 с, хотя давно закончил.
                    // Канал же закрывается, как только процесс завершился
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!stdout.Wait(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        error = "ffprobe не ответил за " + (timeoutMs / 1000) + " с";
                        return null;
                    }
                    stderr.Wait(2000);
                    string output = stdout.Result;
                    if (string.IsNullOrEmpty(output))
                    {
                        // Код выхода — если .NET успел его узнать (см. выше)
                        string code = "";
                        try { if (p.WaitForExit(500)) code = "код " + p.ExitCode; } catch { }
                        string err = stderr.IsCompleted ? (stderr.Result ?? "") : "";
                        error = "ffprobe: " + (code.Length > 0 ? code : "нет вывода") + (err.Length > 0 ? " — " + err.Trim().Split('\n')[0] : "");
                        return null;
                    }
                    return output;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static string QueryParam(string query, string name)
        {
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (key != name) continue;
                string value = eq >= 0 ? pair.Substring(eq + 1) : "";
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return null;
        }

        static void Respond(NetworkStream stream, int code, string reason, string body)
        {
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string head =
                "HTTP/1.1 " + code + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: *\r\n" +
                "Access-Control-Allow-Private-Network: true\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Length: " + data.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            if (data.Length > 0) stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        static string Json(string key, string value)
        {
            return "{" + Quote(key) + ":" + Quote(value ?? "") + "}";
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
