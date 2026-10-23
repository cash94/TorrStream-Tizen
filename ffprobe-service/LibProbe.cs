// libtsprobe.so (native/tsprobe.c) — урезанный FFmpeg внутри процесса службы.
//
// Для Tizen 5.5: там службе запрещено запускать программы — и ffprobe-файл, и
// копию в памяти («Operation not permitted»). Библиотеку же процесс загружает
// к себе (dlopen) — это не запуск новой программы. Путь к .so известен только
// во время работы (каталог пакета), поэтому не [DllImport("libtsprobe.so")],
// а dlopen + dlsym.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TorrStreamFfprobe
{
    public class LibProbe
    {
        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr handle, string name);
        [DllImport("libdl.so.2")] static extern IntPtr dlerror();
        const int RTLD_NOW = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int VersionFn(byte[] buf, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ProbeFn(byte[] url, byte[] buf, int size, int timeoutMs);

        readonly VersionFn versionFn;
        readonly ProbeFn probeFn;

        public LibProbe(string path)
        {
            // Сначала пустой dlerror(): .NET привязывает P/Invoke при первом вызове
            // (сам делает dlopen/dlsym) и этим стирает ошибку нашего dlopen — на
            // Tizen 5.5 вместо причины приходило «?»
            dlerror();
            IntPtr h = dlopen(path, RTLD_NOW);
            if (h == IntPtr.Zero) throw new Exception("dlopen: " + LastDlError());
            versionFn = Get<VersionFn>(h, "tsprobe_version");
            probeFn = Get<ProbeFn>(h, "tsprobe_probe");
        }

        static T Get<T>(IntPtr h, string name) where T : class
        {
            IntPtr p = dlsym(h, name);
            if (p == IntPtr.Zero) throw new Exception("dlsym " + name + ": " + LastDlError());
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static string LastDlError()
        {
            IntPtr e = dlerror();
            return e == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(e);
        }

        public string Version()
        {
            var buf = new byte[256];
            versionFn(buf, buf.Length);
            return Utf8(buf);
        }

        /// <summary>JSON как у ffprobe; ошибка — исключение с текстом библиотеки</summary>
        public string Probe(string url, int timeoutMs)
        {
            byte[] u = Encoding.UTF8.GetBytes(url + "\0");
            int size = 64 * 1024;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new byte[size];
                int r = probeFn(u, buf, size, timeoutMs);
                if (r == 0) return Utf8(buf);
                if (r < 0) throw new Exception(Utf8(buf));
                size = r + 1024;   // мало места — библиотека сказала, сколько нужно
            }
            throw new Exception("ответ библиотеки слишком большой");
        }

        static string Utf8(byte[] buf)
        {
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
        }
    }
}
