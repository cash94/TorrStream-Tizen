// Служба ffprobe для TorrStream на Samsung Tizen — обвязка Tizen.
//
// Веб-приложению Tizen запускать программы нельзя, поэтому ffprobe живёт здесь:
// .NET-служба внутри TorrStream-Tizen.wgt (гибрид, как TorrServer от
// no-cover/torr-streaming) стартует вместе с телевизором и отвечает по HTTP
// на порту 8095 (ProbeServer.cs). Фронтенд TorrStream спрашивает её первой,
// когда ищет названия дорожек файла (player.js: directProbeStart).
//
// ffprobe — статическая сборка armhf (johnvansickle.com), та же, что в
// TorrStream-webOS. Из res/ пакета её копируем в каталог данных и делаем
// исполняемой. Tizen 5.5 запускать файл оттуда не даёт («Operation not
// permitted») — тогда запуск из памяти (MemoryCopy ниже), как у no-cover.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Tizen.Applications;

namespace TorrStreamFfprobe
{
    class Program : ServiceApplication
    {
        const int Port = 8095;
        const string LogTag = "TorrStreamFfprobe";

        ProbeServer server;
        string sourcePath;   // res/ffprobe — из него и копия в память

        protected override void OnCreate()
        {
            base.OnCreate();
            // Сборка для Tizen 5.5 (tools/build.py --lib): вместо ffprobe —
            // libtsprobe.so, FFmpeg внутри процесса. Программы там запускать нельзя
            string libPath = FindLibrary();
            if (libPath != null)
            {
                try
                {
                    var lib = new LibProbe(libPath);
                    string ver = lib.Version();
                    server = new ProbeServer(Port, url => lib.Probe(url, 30000), ver, null, msg => Tizen.Log.Info(LogTag, msg));
                    server.VersionChecked = ShowToast;
                    server.Start();
                    return;
                }
                catch (Exception e)
                {
                    Tizen.Log.Error(LogTag, "libtsprobe: " + e);
                    ShowToast("TorrStream: FFmpeg-библиотека не загрузилась — " + e.Message);
                    // и дальше, как обычно: вдруг в пакете есть и ffprobe
                }
            }
            string stageError;
            string path = StageBinary(out stageError);
            try
            {
                server = new ProbeServer(Port, path, stageError, msg => Tizen.Log.Info(LogTag, msg), MemoryCopy);
                server.VersionChecked = ShowToast;
                server.Start();
                if (path == null) ShowToast("TorrStream: ffprobe не готов — " + (stageError ?? "ошибка"));
            }
            catch (Exception e)
            {
                Tizen.Log.Error(LogTag, "listen: " + e);
            }
        }

        protected override void OnTerminate()
        {
            if (server != null) server.Stop();
            base.OnTerminate();
        }

        /// <summary>
        /// libtsprobe.so в пакете: lib/ рядом с res/ (так её кладёт tools/build.py)
        /// или res/. Нет — null
        /// </summary>
        static string FindLibrary()
        {
            try
            {
                string res = Application.Current.DirectoryInfo.Resource.TrimEnd('/');
                string root = Path.GetDirectoryName(res);
                foreach (var p in new[] { Path.Combine(root, "lib", "libtsprobe.so"), Path.Combine(res, "libtsprobe.so") })
                    if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, uint mode);

        [DllImport("libc", SetLastError = true)]
        static extern int syscall(int number, string name, uint flags);

        [DllImport("libc", SetLastError = true)]
        static extern IntPtr write(int fd, IntPtr buf, UIntPtr count);

        // res/ffprobe → data/ffprobe (только если другой размер: новая версия пакета).
        // Не вышло — запускаем прямо из res/ (а не вышло и там — из памяти)
        string StageBinary(out string error)
        {
            error = null;
            try
            {
                sourcePath = Path.Combine(Application.Current.DirectoryInfo.Resource, "ffprobe");
                string dst = Path.Combine(Application.Current.DirectoryInfo.Data, "ffprobe");
                if (!File.Exists(sourcePath)) { error = "в пакете нет res/ffprobe"; return null; }
                try
                {
                    if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(sourcePath).Length)
                        File.Copy(sourcePath, dst, true);
                    chmod(dst, 0x1ED); // 0755
                    return dst;
                }
                catch (Exception e)
                {
                    Tizen.Log.Error(LogTag, "stage: " + e);
                    return sourcePath;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                Tizen.Log.Error(LogTag, "stage: " + e);
                return null;
            }
        }

        /// <summary>
        /// Копия ffprobe в анонимном файле в памяти (memfd_create) — путь
        /// /proc/self/fd/N. Флагов нет (без CLOEXEC): дочерний процесс наследует
        /// дескриптор, и /proc/self/fd/N в нём указывает на ту же копию.
        /// Номер системного вызова — свой у каждой архитектуры.
        /// </summary>
        string MemoryCopy()
        {
            if (sourcePath == null || !File.Exists(sourcePath)) throw new Exception("нет res/ffprobe");
            int nr;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.Arm: nr = 385; break;
                case Architecture.Arm64: nr = 279; break;
                case Architecture.X64: nr = 319; break;
                case Architecture.X86: nr = 356; break;
                default: throw new Exception("memfd_create: неизвестная архитектура " + RuntimeInformation.ProcessArchitecture);
            }
            int fd = syscall(nr, "ffprobe", 0);
            if (fd < 0) throw new Exception("memfd_create: errno " + Marshal.GetLastWin32Error());

            byte[] data = File.ReadAllBytes(sourcePath);
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = pin.AddrOfPinnedObject();
                long done = 0;
                while (done < data.Length)
                {
                    long n = (long)write(fd, new IntPtr(basePtr.ToInt64() + done), new UIntPtr((ulong)(data.Length - done)));
                    if (n <= 0) throw new Exception("memfd write: errno " + Marshal.GetLastWin32Error() + " на " + done + " байте");
                    done += n;
                }
            }
            finally { pin.Free(); }
            return "/proc/self/fd/" + fd;
        }

        /// <summary>
        /// Всплывающее уведомление на экране телевизора — системное окно
        /// org.tizen.alert-syspopup (так сообщает о запуске TorrServer служба
        /// no-cover). Видно, что служба поднялась и запускается ли ffprobe, —
        /// без захода в «Об устройстве»
        /// </summary>
        static void ShowToast(string text)
        {
            try
            {
                var ctrl = new AppControl { ApplicationId = "org.tizen.alert-syspopup" };
                ctrl.ExtraData.Add("type", "toast");
                ctrl.ExtraData.Add("timeout", "8");
                ctrl.ExtraData.Add("text", text);
                AppControl.SendLaunchRequest(ctrl);
            }
            catch (Exception e)
            {
                Tizen.Log.Error(LogTag, "toast: " + e.Message);
            }
        }

        static void Main(string[] args)
        {
            new Program().Run(args);
        }
    }
}
