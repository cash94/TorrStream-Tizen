// Разбор заголовка MKV и MP4 на C# — замена ffprobe там, где своё нативное
// запускать нельзя. На Tizen 5.5 службе запрещено и запускать программы (файл,
// memfd — «Operation not permitted»), и грузить свою .so («failed to map
// segment from shared object»): исполняется только подписанный код. Управляемый
// код службы при этом работает — им и читаем заголовок.
//
// Названия дорожек, язык, кодек, флаги default/forced, каналы, размер кадра —
// всё в заголовке контейнера: у MKV это Tracks (первые сотни килобайт), у MP4 —
// moov (в начале или в конце файла). Файл читаем с TorrServer запросами Range
// кусками по 256 КБ, только нужные места. Ответ — в том же виде, что у
// «ffprobe -print_format json -show_streams -show_format» (поля, которые читает
// фронтенд: player.js: applyDirectProbe), имена кодеков — как у ffprobe
// (player.js: TEXT_SUB_CODECS, HIDDEN_AUDIO_CODECS).
// Прочие контейнеры (AVI, TS) — ошибка: фронтенд спросит CUB и сервер.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace TorrStreamFfprobe
{
    public static class HeaderProbe
    {
        public const string Version = "разбор заголовка MKV/MP4 1.0";

        class Track
        {
            public string Type, Codec, Language, Title, Handler;
            public int Width, Height, Channels, SampleRate;
            public bool Default, Forced, HearingImpaired, VisualImpaired, Commentary, Original;
        }

        /// <summary>JSON как у ffprobe; ошибка — исключение с текстом для /ffprobe</summary>
        public static string Probe(string url, int timeoutMs)
        {
            var src = new RangeSource(url, timeoutMs);
            byte[] head = src.Read(0, 12);
            if (head.Length < 12) throw new Exception("файл пустой или TorrServer его не отдал");
            var tracks = new List<Track>();
            string format;
            double duration;
            if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
            {
                format = "matroska,webm";
                duration = Mkv.Parse(src, tracks);
            }
            else if (Mp4.IsAtom(Encoding.ASCII.GetString(head, 4, 4)))
            {
                format = "mov,mp4,m4a,3gp,3g2,mj2";
                duration = Mp4.Parse(src, tracks);
            }
            else throw new Exception("контейнер не MKV и не MP4 — разбирает только ffprobe");
            return ToJson(tracks, format, duration, src.Requests);
        }

        static string ToJson(List<Track> tracks, string format, double duration, int requests)
        {
            var sb = new StringBuilder("{\"streams\":[");
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(i)
                  .Append(",\"codec_name\":").Append(ProbeServer.Quote(t.Codec ?? "unknown"))
                  .Append(",\"codec_type\":").Append(ProbeServer.Quote(t.Type));
                if (t.Width > 0) sb.Append(",\"width\":").Append(t.Width).Append(",\"height\":").Append(t.Height);
                if (t.Channels > 0) sb.Append(",\"channels\":").Append(t.Channels);
                if (t.SampleRate > 0) sb.Append(",\"sample_rate\":\"").Append(t.SampleRate).Append('"');
                sb.Append(",\"disposition\":{\"default\":").Append(t.Default ? 1 : 0)
                  .Append(",\"forced\":").Append(t.Forced ? 1 : 0)
                  .Append(",\"hearing_impaired\":").Append(t.HearingImpaired ? 1 : 0)
                  .Append(",\"visual_impaired\":").Append(t.VisualImpaired ? 1 : 0)
                  .Append(",\"comment\":").Append(t.Commentary ? 1 : 0)
                  .Append(",\"original\":").Append(t.Original ? 1 : 0).Append('}');
                // Без языка — без тега (как ffprobe у MKV с «und»)
                var tags = new List<string>();
                if (t.Language != null) tags.Add("\"language\":" + ProbeServer.Quote(t.Language));
                if (!string.IsNullOrEmpty(t.Title)) tags.Add("\"title\":" + ProbeServer.Quote(t.Title));
                if (!string.IsNullOrEmpty(t.Handler)) tags.Add("\"handler_name\":" + ProbeServer.Quote(t.Handler));
                sb.Append(",\"tags\":{").Append(string.Join(",", tags)).Append("}}");
            }
            sb.Append("],\"format\":{\"format_name\":").Append(ProbeServer.Quote(format))
              .Append(",\"nb_streams\":").Append(tracks.Count);
            if (duration > 0) sb.Append(",\"duration\":\"").Append(duration.ToString("0.000000", CultureInfo.InvariantCulture)).Append('"');
            // Чем разобрано и сколько запросов к TorrServer понадобилось
            sb.Append(",\"probe\":").Append(ProbeServer.Quote(Version)).Append(",\"requests\":").Append(requests);
            return sb.Append("}}").ToString();
        }

        // ---------- чтение файла с TorrServer кусками (Range) ----------

        class RangeSource
        {
            const int Chunk = 256 * 1024;
            readonly string url;
            readonly string auth;   // Basic из адреса (user:pass@) — сам HttpWebRequest его не шлёт
            readonly DateTime deadline;
            readonly Dictionary<long, byte[]> chunks = new Dictionary<long, byte[]>();
            public long Length = -1;   // из Content-Range; -1 — ещё не знаем
            public int Requests;

            public RangeSource(string url, int timeoutMs)
            {
                this.url = url;
                // Пароль TorrServer фронтенд кладёт в адрес (player.js: directProbeUri)
                var uri = new Uri(url);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    string[] up = uri.UserInfo.Split(new[] { ':' }, 2);
                    string plain = Uri.UnescapeDataString(up[0]) + ":" + (up.Length > 1 ? Uri.UnescapeDataString(up[1]) : "");
                    auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
                    this.url = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
                }
                deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            }

            /// <summary>count байт с offset; у конца файла — меньше</summary>
            public byte[] Read(long offset, int count)
            {
                if (Length >= 0)
                {
                    if (offset >= Length) return new byte[0];
                    if (offset + count > Length) count = (int)(Length - offset);
                }
                var result = new byte[count];
                int done = 0;
                while (done < count)
                {
                    long pos = offset + done;
                    long ci = pos / Chunk;
                    byte[] c = GetChunk(ci);
                    int inChunk = (int)(pos - ci * Chunk);
                    if (inChunk >= c.Length) break;   // конец файла
                    int n = Math.Min(count - done, c.Length - inChunk);
                    Buffer.BlockCopy(c, inChunk, result, done, n);
                    done += n;
                    if (c.Length < Chunk) break;
                }
                if (done < count) Array.Resize(ref result, done);
                return result;
            }

            byte[] GetChunk(long ci)
            {
                byte[] c;
                if (chunks.TryGetValue(ci, out c)) return c;
                long start = ci * Chunk;
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) throw new Exception("TorrServer не отдал заголовок вовремя");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.AddRange(start, start + Chunk - 1);
                if (auth != null) req.Headers[HttpRequestHeader.Authorization] = auth;
                req.Timeout = left;
                req.ReadWriteTimeout = left;
                Requests++;
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                        if (partial)
                        {
                            // Content-Range: bytes 0-262143/1234567890
                            string cr = resp.Headers["Content-Range"] ?? "";
                            int slash = cr.LastIndexOf('/');
                            long total;
                            if (slash >= 0 && long.TryParse(cr.Substring(slash + 1), out total)) Length = total;
                        }
                        else
                        {
                            // Весь файл вместо куска: начало взять можно, середину — нет
                            if (start > 0) throw new Exception("TorrServer не отдаёт файл кусками (Range)");
                            if (resp.ContentLength > 0) Length = resp.ContentLength;
                        }
                        using (var s = resp.GetResponseStream())
                        {
                            var buf = new byte[Chunk];
                            int got = 0, n;
                            while (got < Chunk && (n = s.Read(buf, got, Chunk - got)) > 0) got += n;
                            if (got < Chunk) Array.Resize(ref buf, got);
                            c = buf;
                        }
                        if (!partial) req.Abort();   // остаток файла не нужен
                    }
                }
                catch (WebException e)
                {
                    var r = e.Response as HttpWebResponse;
                    // 416 — просили за концом файла
                    if (r != null && (int)r.StatusCode == 416) c = new byte[0];
                    else throw new Exception("TorrServer: " + e.Message);
                }
                chunks[ci] = c;
                return c;
            }
        }

        // ---------- Matroska (EBML) ----------

        static class Mkv
        {
            const long IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdSeek = 0x4DBB,
                IdSeekId = 0x53AB, IdSeekPos = 0x53AC, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B, IdCluster = 0x1F43B675,
                IdTimestampScale = 0x2AD7B1, IdDuration = 0x4489, IdTrackEntry = 0xAE, IdTrackType = 0x83,
                IdFlagDefault = 0x88, IdFlagForced = 0x55AA, IdFlagHearing = 0x55AB, IdFlagVisual = 0x55AC,
                IdFlagOriginal = 0x55AE, IdFlagCommentary = 0x55AF, IdName = 0x536E, IdLanguage = 0x22B59C,
                IdLanguageIetf = 0x22B59D, IdCodecId = 0x86, IdVideo = 0xE0, IdPixelWidth = 0xB0, IdPixelHeight = 0xBA,
                IdAudio = 0xE1, IdSamplingFreq = 0xB5, IdChannels = 0x9F;
            const int MaxElement = 8 * 1024 * 1024;   // Tracks/Info/SeekHead больше не бывают

            /// <summary>Дорожки в tracks, вернёт длительность в секундах (0 — нет)</summary>
            public static double Parse(RangeSource src, List<Track> tracks)
            {
                long pos = 0, id, size;
                Header(src, ref pos, out id, out size);
                if (id != IdEbml || size < 0) throw new Exception("MKV: нет заголовка EBML");
                pos += size;
                Header(src, ref pos, out id, out size);
                if (id != IdSegment) throw new Exception("MKV: нет Segment");
                long segStart = pos, segEnd = size < 0 ? long.MaxValue : pos + size;

                byte[] info = null, tracksData = null;
                var seeks = new Dictionary<long, long>();
                // Верхний уровень Segment — до первого Cluster: дальше идут кадры
                for (int guard = 0; guard < 64 && pos < segEnd; guard++)
                {
                    long elStart = pos;
                    if (!TryHeader(src, ref pos, out id, out size)) break;
                    if (id == IdCluster || size < 0) break;
                    if (id == IdSeekHead) ReadSeekHead(Body(src, pos, size), seeks);
                    else if (id == IdInfo) info = Body(src, pos, size);
                    else if (id == IdTracks) { tracksData = Body(src, pos, size); }
                    if (tracksData != null && info != null) break;
                    pos += size;
                    if (pos <= elStart) break;
                }
                // Tracks/Info после кадров — по оглавлению (SeekHead)
                long at;
                if (tracksData == null && seeks.TryGetValue(IdTracks, out at)) tracksData = Element(src, segStart + at, IdTracks);
                if (info == null && seeks.TryGetValue(IdInfo, out at)) info = Element(src, segStart + at, IdInfo);
                if (tracksData == null) throw new Exception("MKV: не нашёл список дорожек (Tracks)");

                foreach (var e in Children(tracksData, 0, tracksData.Length))
                    if (e.Id == IdTrackEntry) { var t = ReadTrack(tracksData, e.Pos, e.Pos + e.Size); if (t != null) tracks.Add(t); }

                double duration = 0;
                if (info != null)
                {
                    double scale = 1000000, dur = 0;
                    foreach (var e in Children(info, 0, info.Length))
                    {
                        if (e.Id == IdTimestampScale) scale = UInt(info, e);
                        else if (e.Id == IdDuration) dur = Float(info, e);
                    }
                    duration = dur * scale / 1e9;
                }
                return duration;
            }

            static Track ReadTrack(byte[] b, int start, int end)
            {
                var t = new Track { Default = true, Language = null };
                ulong type = 0;
                string lang = null, ietf = null, codec = null;
                foreach (var e in Children(b, start, end))
                {
                    switch (e.Id)
                    {
                        case IdTrackType: type = UInt(b, e); break;
                        case IdFlagDefault: t.Default = UInt(b, e) != 0; break;
                        case IdFlagForced: t.Forced = UInt(b, e) != 0; break;
                        case IdFlagHearing: t.HearingImpaired = UInt(b, e) != 0; break;
                        case IdFlagVisual: t.VisualImpaired = UInt(b, e) != 0; break;
                        case IdFlagOriginal: t.Original = UInt(b, e) != 0; break;
                        case IdFlagCommentary: t.Commentary = UInt(b, e) != 0; break;
                        case IdName: t.Title = Str(b, e); break;
                        case IdLanguage: lang = Str(b, e); break;
                        case IdLanguageIetf: ietf = Str(b, e); break;
                        case IdCodecId: codec = Str(b, e); break;
                        case IdVideo:
                            foreach (var v in Children(b, e.Pos, e.Pos + e.Size))
                            {
                                if (v.Id == IdPixelWidth) t.Width = (int)UInt(b, v);
                                else if (v.Id == IdPixelHeight) t.Height = (int)UInt(b, v);
                            }
                            break;
                        case IdAudio:
                            foreach (var a in Children(b, e.Pos, e.Pos + e.Size))
                            {
                                if (a.Id == IdSamplingFreq) t.SampleRate = (int)Float(b, a);
                                else if (a.Id == IdChannels) t.Channels = (int)UInt(b, a);
                            }
                            if (t.Channels == 0) t.Channels = 1;   // по спецификации
                            if (t.SampleRate == 0) t.SampleRate = 8000;
                            break;
                    }
                }
                t.Type = type == 1 ? "video" : type == 2 ? "audio" : type == 0x11 ? "subtitle" : "data";
                // Как у ffprobe: Language (по умолчанию «eng»); только IETF — его;
                // «und» ffprobe у MKV не пишет
                t.Language = lang ?? ietf ?? "eng";
                if (t.Language == "und") t.Language = null;
                t.Codec = CodecName(codec ?? "");
                return t;
            }

            static string CodecName(string id)
            {
                switch (id)
                {
                    case "V_MPEG4/ISO/AVC": return "h264";
                    case "V_MPEGH/ISO/HEVC": return "hevc";
                    case "V_AV1": return "av1";
                    case "V_VP9": return "vp9";
                    case "V_VP8": return "vp8";
                    case "V_MPEG1": return "mpeg1video";
                    case "V_MPEG2": return "mpeg2video";
                    case "V_MPEG4/ISO/ASP": case "V_MPEG4/ISO/SP": case "V_MPEG4/ISO/AP": return "mpeg4";
                    case "V_MPEG4/MS/V3": return "msmpeg4v3";
                    case "V_MS/VFW/FOURCC": return "vfw";
                    case "A_AC3": case "A_AC3/BSID9": case "A_AC3/BSID10": return "ac3";
                    case "A_EAC3": return "eac3";
                    case "A_TRUEHD": return "truehd";
                    case "A_MLP": return "mlp";
                    case "A_MPEG/L3": return "mp3";
                    case "A_MPEG/L2": return "mp2";
                    case "A_MPEG/L1": return "mp1";
                    case "A_FLAC": return "flac";
                    case "A_OPUS": return "opus";
                    case "A_VORBIS": return "vorbis";
                    case "A_ALAC": return "alac";
                    case "A_PCM/INT/LIT": return "pcm_s16le";
                    case "A_PCM/INT/BIG": return "pcm_s16be";
                    case "A_PCM/FLOAT/IEEE": return "pcm_f32le";
                    case "S_TEXT/UTF8": case "S_TEXT/ASCII": return "subrip";
                    case "S_TEXT/ASS": case "S_TEXT/SSA": case "S_ASS": case "S_SSA": return "ass";
                    case "S_TEXT/WEBVTT": case "D_WEBVTT/SUBTITLES": return "webvtt";
                    case "S_HDMV/PGS": return "hdmv_pgs_subtitle";
                    case "S_HDMV/TEXTST": return "hdmv_text_subtitle";
                    case "S_VOBSUB": return "dvd_subtitle";
                    case "S_DVBSUB": return "dvb_subtitle";
                    case "S_ARIBSUB": return "arib_caption";
                }
                if (id.StartsWith("A_AAC", StringComparison.Ordinal)) return "aac";
                if (id.StartsWith("A_DTS", StringComparison.Ordinal)) return "dts";
                return id.Length > 0 ? id.ToLowerInvariant() : "unknown";
            }

            static void ReadSeekHead(byte[] b, Dictionary<long, long> seeks)
            {
                foreach (var s in Children(b, 0, b.Length))
                {
                    if (s.Id != IdSeek) continue;
                    long sid = 0, spos = -1;
                    foreach (var e in Children(b, s.Pos, s.Pos + s.Size))
                    {
                        if (e.Id == IdSeekId) sid = (long)UInt(b, e);
                        else if (e.Id == IdSeekPos) spos = (long)UInt(b, e);
                    }
                    if (sid != 0 && spos >= 0 && !seeks.ContainsKey(sid)) seeks[sid] = spos;
                }
            }

            /// <summary>Тело элемента с ожидаемым id по адресу из SeekHead</summary>
            static byte[] Element(RangeSource src, long pos, long expectId)
            {
                long id, size;
                if (!TryHeader(src, ref pos, out id, out size) || id != expectId || size < 0) return null;
                return Body(src, pos, size);
            }

            static byte[] Body(RangeSource src, long pos, long size)
            {
                if (size < 0 || size > MaxElement) throw new Exception("MKV: слишком большой элемент заголовка");
                return src.Read(pos, (int)size);
            }

            static void Header(RangeSource src, ref long pos, out long id, out long size)
            {
                if (!TryHeader(src, ref pos, out id, out size)) throw new Exception("MKV: файл обрывается");
            }

            // id и размер элемента в файле; size −1 — «неизвестен» (поток)
            static bool TryHeader(RangeSource src, ref long pos, out long id, out long size)
            {
                id = size = 0;
                byte[] h = src.Read(pos, 12);
                int p = 0;
                bool unknown;
                if (!Vint(h, ref p, true, out id, out unknown)) return false;
                if (!Vint(h, ref p, false, out size, out unknown)) return false;
                if (unknown) size = -1;
                pos += p;
                return true;
            }

            static bool Vint(byte[] b, ref int p, bool keepMarker, out long value, out bool unknown)
            {
                value = 0;
                unknown = false;
                if (p >= b.Length) return false;
                int first = b[p], len = 1, mask = 0x80;
                while (len <= 8 && (first & mask) == 0) { len++; mask >>= 1; }
                if (len > 8 || p + len > b.Length) return false;
                long v = keepMarker ? first : (first & (mask - 1));
                bool ones = (first & (mask - 1)) == mask - 1;
                for (int i = 1; i < len; i++)
                {
                    v = (v << 8) | b[p + i];
                    if (b[p + i] != 0xFF) ones = false;
                }
                p += len;
                value = v;
                unknown = !keepMarker && ones;
                return true;
            }

            struct El { public long Id; public int Pos, Size; }

            static List<El> Children(byte[] b, int start, int end)
            {
                var list = new List<El>();
                int p = start;
                while (p < end)
                {
                    long id, size;
                    bool unknown;
                    if (!Vint(b, ref p, true, out id, out unknown)) break;
                    if (!Vint(b, ref p, false, out size, out unknown)) break;
                    if (unknown || size < 0 || p + size > end) break;
                    list.Add(new El { Id = id, Pos = p, Size = (int)size });
                    p += (int)size;
                }
                return list;
            }

            static ulong UInt(byte[] b, El e)
            {
                ulong v = 0;
                for (int i = 0; i < e.Size && i < 8; i++) v = (v << 8) | b[e.Pos + i];
                return v;
            }

            static double Float(byte[] b, El e)
            {
                if (e.Size == 4)
                {
                    var f = new byte[4];
                    Array.Copy(b, e.Pos, f, 0, 4);
                    if (BitConverter.IsLittleEndian) Array.Reverse(f);
                    return BitConverter.ToSingle(f, 0);
                }
                if (e.Size == 8)
                {
                    var d = new byte[8];
                    Array.Copy(b, e.Pos, d, 0, 8);
                    if (BitConverter.IsLittleEndian) Array.Reverse(d);
                    return BitConverter.ToDouble(d, 0);
                }
                return 0;
            }

            static string Str(byte[] b, El e)
            {
                return Encoding.UTF8.GetString(b, e.Pos, e.Size).TrimEnd('\0').Trim();
            }
        }

        // ---------- MP4 / MOV (атомы) ----------

        static class Mp4
        {
            static readonly HashSet<string> TopAtoms = new HashSet<string> { "ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "styp", "sidx" };

            public static bool IsAtom(string type) { return TopAtoms.Contains(type); }

            struct Atom { public string Type; public long Pos, Size; }   // Pos/Size — тело атома

            public static double Parse(RangeSource src, List<Track> tracks)
            {
                // moov в начале (faststart) или после mdat — идём по атомам верхнего
                // уровня, перешагивая mdat по размеру (читаем только заголовки)
                Atom? moov = null;
                long pos = 0;
                for (int guard = 0; guard < 64; guard++)
                {
                    Atom a;
                    if (!ReadAtom(src, pos, long.MaxValue, out a)) break;
                    if (a.Type == "moov") { moov = a; break; }
                    if (a.Size < 0) break;
                    pos = a.Pos + a.Size;
                }
                if (moov == null) throw new Exception("MP4: нет заголовка (moov)");

                double duration = 0;
                foreach (var a in Children(src, moov.Value))
                {
                    if (a.Type == "mvhd")
                    {
                        byte[] b = src.Read(a.Pos, 32);
                        if (b.Length < 20) continue;
                        bool v1 = b[0] == 1;
                        long timescale = v1 ? U32(b, 20) : U32(b, 12);
                        long dur = v1 ? (b.Length >= 32 ? (long)U64(b, 24) : 0) : U32(b, 16);
                        if (timescale > 0) duration = (double)dur / timescale;
                    }
                    else if (a.Type == "trak")
                    {
                        var t = ReadTrak(src, a);
                        if (t != null) tracks.Add(t);
                    }
                }
                return duration;
            }

            static Track ReadTrak(RangeSource src, Atom trak)
            {
                var t = new Track();
                string handler = null, entry = null;
                foreach (var a in Children(src, trak))
                {
                    if (a.Type == "tkhd")
                    {
                        byte[] b = src.Read(a.Pos, 4);
                        // флаг «дорожка включена» — у ffprobe это disposition.default
                        if (b.Length == 4) t.Default = (b[3] & 1) != 0;
                    }
                    else if (a.Type == "udta")
                    {
                        foreach (var u in Children(src, a))
                        {
                            if (u.Type == "name" && u.Size > 0 && u.Size < 4096) t.Title = Text(src.Read(u.Pos, (int)u.Size));
                            else if (u.Type == "kind" && u.Size > 4 && u.Size < 4096) ReadKind(src.Read(u.Pos, (int)u.Size), t);
                        }
                    }
                    else if (a.Type == "mdia")
                    {
                        foreach (var m in Children(src, a))
                        {
                            if (m.Type == "mdhd")
                            {
                                byte[] b = src.Read(m.Pos, 32);
                                int off = b.Length > 0 && b[0] == 1 ? 32 : 20;
                                if (b.Length >= off + 2) t.Language = Lang((b[off] << 8) | b[off + 1]);
                                else if (b.Length > 0 && b[0] == 1) { byte[] l = src.Read(m.Pos + 32, 2); if (l.Length == 2) t.Language = Lang((l[0] << 8) | l[1]); }
                            }
                            else if (m.Type == "hdlr" && m.Size >= 24 && m.Size < 4096)
                            {
                                byte[] b = src.Read(m.Pos, (int)m.Size);
                                handler = Encoding.ASCII.GetString(b, 8, 4);
                                t.Handler = HandlerName(b, 24);
                            }
                            else if (m.Type == "minf")
                            {
                                foreach (var s in Children(src, m))
                                    if (s.Type == "stbl")
                                        foreach (var d in Children(src, s))
                                            if (d.Type == "stsd") entry = ReadSampleEntry(src, d, t);
                            }
                        }
                    }
                }
                switch (handler)
                {
                    case "vide": t.Type = "video"; break;
                    case "soun": t.Type = "audio"; break;
                    case "subt": case "sbtl": case "text": case "clcp": t.Type = "subtitle"; break;
                    default: t.Type = "data"; break;
                }
                if (t.Language == null) t.Language = "und";
                t.Codec = CodecName(entry ?? "");
                return t;
            }

            // Первая запись stsd: тип (avc1, mp4a…), размер кадра или каналы и частота
            static string ReadSampleEntry(RangeSource src, Atom stsd, Track t)
            {
                byte[] b = src.Read(stsd.Pos, 8 + 8 + 28);
                if (b.Length < 16) return null;
                string type = Encoding.ASCII.GetString(b, 12, 4);
                int e = 16;   // тело записи: 6 резерв + 2 data_reference_index
                if (b.Length >= e + 28)
                {
                    if (type == "avc1" || type == "avc3" || type == "hvc1" || type == "hev1" || type == "dvh1" || type == "dvhe" ||
                        type == "av01" || type == "vp09" || type == "mp4v")
                    {
                        t.Width = (b[e + 24] << 8) | b[e + 25];
                        t.Height = (b[e + 26] << 8) | b[e + 27];
                    }
                    else if (type == "mp4a" || type == "ac-3" || type == "ec-3" || type == "Opus" || type == "fLaC" ||
                             type == "alac" || type == "dtsc" || type == "dtsh" || type == "dtsl" || type == "dtse" ||
                             type == ".mp3" || type == "mlpa" || type == "ac-4")
                    {
                        t.Channels = (b[e + 16] << 8) | b[e + 17];
                        t.SampleRate = (int)(U32(b, e + 24) >> 16);
                    }
                }
                return type;
            }

            // kind (роль дорожки, DASH): ffmpeg так пишет «комментарий» и
            // «принудительные субтитры» — ffprobe читает их в disposition
            static void ReadKind(byte[] b, Track t)
            {
                // версия/флаги 4, схема , значение 
                int z = Array.IndexOf(b, (byte)0, 4);
                if (z < 0) return;
                string value = Text(b, z + 1, b.Length - z - 1);
                if (value == "commentary") t.Commentary = true;
                else if (value == "forced-subtitle") t.Forced = true;
                else if (value == "main") t.Default = true;
            }

            static string CodecName(string type)
            {
                switch (type)
                {
                    case "avc1": case "avc3": return "h264";
                    case "hvc1": case "hev1": case "dvh1": case "dvhe": return "hevc";
                    case "av01": return "av1";
                    case "vp09": return "vp9";
                    case "mp4v": return "mpeg4";
                    case "mp4a": return "aac";
                    case ".mp3": return "mp3";
                    case "ac-3": return "ac3";
                    case "ec-3": return "eac3";
                    case "ac-4": return "ac4";
                    case "dtsc": case "dtsh": case "dtsl": case "dtse": return "dts";
                    case "mlpa": return "truehd";
                    case "Opus": return "opus";
                    case "fLaC": return "flac";
                    case "alac": return "alac";
                    case "tx3g": case "text": return "mov_text";
                    case "wvtt": return "webvtt";
                    case "stpp": return "ttml";
                    case "c608": return "eia_608";
                }
                return type.Length > 0 ? type.Trim().ToLowerInvariant() : "unknown";
            }

            // ISO-639-2/T упакован по 5 бит на букву
            static string Lang(int packed)
            {
                if (packed == 0 || packed == 0x7FFF || packed < 0x400) return "und";
                var c = new char[3];
                for (int i = 0; i < 3; i++) c[i] = (char)(((packed >> (10 - 5 * i)) & 0x1F) + 0x60);
                return new string(c);
            }

            static string HandlerName(byte[] b, int start)
            {
                if (start >= b.Length) return null;
                int len = b.Length - start;
                // QuickTime: строка Паскаля (первый байт — длина)
                if (b[start] == len - 1 && len > 1) { start++; len--; }
                return Text(b, start, len);
            }

            static string Text(byte[] b) { return Text(b, 0, b.Length); }

            static string Text(byte[] b, int start, int len)
            {
                string s = Encoding.UTF8.GetString(b, start, len);
                int z = s.IndexOf('\0');
                return (z >= 0 ? s.Substring(0, z) : s).Trim();
            }

            static List<Atom> Children(RangeSource src, Atom parent)
            {
                var list = new List<Atom>();
                long pos = parent.Pos, end = parent.Pos + parent.Size;
                for (int guard = 0; guard < 4096 && pos + 8 <= end; guard++)
                {
                    Atom a;
                    if (!ReadAtom(src, pos, end, out a) || a.Size < 0) break;
                    list.Add(a);
                    pos = a.Pos + a.Size;
                }
                return list;
            }

            static bool ReadAtom(RangeSource src, long pos, long end, out Atom a)
            {
                a = new Atom();
                byte[] h = src.Read(pos, 16);
                if (h.Length < 8) return false;
                long size = U32(h, 0);
                int head = 8;
                if (size == 1)
                {
                    if (h.Length < 16) return false;
                    size = (long)U64(h, 8);
                    head = 16;
                }
                a.Type = Encoding.ASCII.GetString(h, 4, 4);
                a.Pos = pos + head;
                if (size == 0) a.Size = src.Length >= 0 ? src.Length - a.Pos : -1;   // до конца файла
                else if (size < head) return false;
                else a.Size = size - head;
                if (end != long.MaxValue && a.Size >= 0 && a.Pos + a.Size > end) return false;
                return true;
            }

            static long U32(byte[] b, int p)
            {
                return ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
            }

            static ulong U64(byte[] b, int p)
            {
                return ((ulong)U32(b, p) << 32) | (ulong)U32(b, p + 4);
            }
        }
    }
}
