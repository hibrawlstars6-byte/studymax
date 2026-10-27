// Study Max app: opens the page in its own window and tells it what Windows is playing.
// Built with the C# compiler that ships with Windows (.NET Framework 4), so it is C# 5: no newer syntax.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

static class App
{
    const string Url = "http://localhost:8765/";
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static GlobalSystemMediaTransportControlsSessionManager mgr;

    [MTAThread]
    static void Main()
    {
        try { Run(); }
        catch (Exception e) { File.AppendAllText(Path.Combine(Dir, "error.txt"), DateTime.Now + "  " + e + "\r\n"); }
    }

    static HttpListener Listen()
    {
        var l = new HttpListener();
        l.Prefixes.Add(Url);
        l.Start();
        return l;
    }

    static void Run()
    {
        HttpListener l;
        try { l = Listen(); }
        catch (HttpListenerException)
        {
            // an older copy is running: ask it to quit so this one takes over
            try { using (var w = new WebClient()) w.DownloadString(Url + "quit"); } catch { }
            Thread.Sleep(1000);
            try { l = Listen(); } catch (HttpListenerException) { Open(); return; }
        }
        mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        Open();
        var last = DateTime.Now;
        while (true)
        {
            var t = l.GetContextAsync();
            // quit by itself once the window has been closed a while (a minimized window still checks in about once a minute)
            while (!t.Wait(1000))
                if ((DateTime.Now - last).TotalSeconds > 150) return;
            last = DateTime.Now;
            if (!Handle(t.Result)) return;
        }
    }

    static void Open()
    {
        try { Process.Start("msedge", "--app=" + Url); } catch { Process.Start(Url); }
        // Edge remembers the last window size, so if the app was closed in mini mode it would reopen tiny.
        // Wait for the window, then put it back to a normal size (and never always-on-top).
        new Thread(() =>
        {
            for (int k = 0; k < 75; k++)
            {
                Thread.Sleep(200);
                var h = FindWindow(null, "Study Max");
                if (h == IntPtr.Zero) continue;
                Rect r;
                if (GetWindowRect(h, out r) && (r.R - r.L < 700 || r.B - r.T < 500))
                {
                    var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    int w = Math.Min(1100, wa.Width), ht = Math.Min(720, wa.Height);
                    SetWindowPos(h, new IntPtr(-2), wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - ht) / 2, w, ht, 0x40);
                }
                else SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x43);
                return;
            }
        }) { IsBackground = true }.Start();
    }

    static bool Handle(HttpListenerContext c)
    {
        string p = c.Request.Url.AbsolutePath, type = "application/json";
        byte[] body;
        try
        {
            if (p == "/quit") { c.Response.Close(); return false; }
            if (p == "/art")
            {
                body = Art();
                if (body == null) { c.Response.StatusCode = 404; c.Response.Close(); return true; }
                type = "image/png";
            }
            else if (p == "/now") body = Utf(Now());
            else if (p.StartsWith("/cmd/")) { Cmd(p.Substring(5)); body = Utf("{\"ok\":true}"); }
            else { body = Html(); type = "text/html; charset=utf-8"; }
        }
        catch (Exception e) { body = Utf("{\"title\":null,\"error\":" + Js(e.Message) + "}"); type = "application/json"; }
        try
        {
            c.Response.ContentType = type;
            c.Response.ContentLength64 = body.Length;
            c.Response.OutputStream.Write(body, 0, body.Length);
            c.Response.Close();
        }
        catch { } // window closed mid-reply
        return true;
    }

    // the app that is actually playing (Windows' "current" one can be a paused or different app)
    static GlobalSystemMediaTransportControlsSession Session()
    {
        foreach (var s in mgr.GetSessions())
            if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
        return mgr.GetCurrentSession();
    }

    static string Now()
    {
        var s = Session();
        if (s == null) return "{\"title\":null}";
        var m = Await(s.TryGetMediaPropertiesAsync());
        bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        // Windows only updates the position on play/pause/seek, so add the time since then
        var tl = s.GetTimelineProperties();
        double dur = tl.EndTime.TotalSeconds, pos = tl.Position.TotalSeconds;
        if (playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        pos = Math.Max(0, Math.Min(dur, pos));
        var ci = CultureInfo.InvariantCulture;
        return "{\"title\":" + Js(m.Title) + ",\"artist\":" + Js(m.Artist) + ",\"playing\":" + (playing ? "true" : "false")
            + ",\"pos\":" + pos.ToString("0.0", ci) + ",\"dur\":" + dur.ToString("0.0", ci) + ",\"vol\":" + VolSafe().ToString("0.00", ci) + "}";
    }

    static byte[] Art()
    {
        var s = Session();
        if (s == null) return null;
        var m = Await(s.TryGetMediaPropertiesAsync());
        if (m.Thumbnail == null) return null;
        var st = Await(m.Thumbnail.OpenReadAsync());
        var reader = new DataReader(st.GetInputStreamAt(0));
        Await(reader.LoadAsync((uint)st.Size));
        var bytes = new byte[st.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    // mini mode: shrink the app window to a small strip in the bottom-right corner and keep it on top
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
    static Rect before;
    static bool hasBefore;

    static void Mini(bool on)
    {
        var h = FindWindow(null, "Study Max");
        if (h == IntPtr.Zero) return;
        if (on)
        {
            if (GetWindowRect(h, out before)) hasBefore = true;
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int w = 440, ht = 200;
            SetWindowPos(h, new IntPtr(-1), wa.Right - w - 16, wa.Bottom - ht - 16, w, ht, 0x40); // topmost
        }
        else
        {
            if (hasBefore && before.R - before.L >= 700 && before.B - before.T >= 500) SetWindowPos(h, new IntPtr(-2), before.L, before.T, before.R - before.L, before.B - before.T, 0x40);
            else
            {
                var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(1100, wa.Width), ht = Math.Min(720, wa.Height);
                SetWindowPos(h, new IntPtr(-2), wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - ht) / 2, w, ht, 0x40);
            }
        }
    }

    // --- per-app volume through Windows' volume mixer (Core Audio) ---
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { int NotUsed1(); [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 { int NotUsed1(); int NotUsed2(); [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e); }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s); }
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int N1(); int N2(); int N3(); int N4(); int N5(); int N6(); int N7(); int N8(); int N9(); int N10(); int N11();
        [PreserveSig] int GetProcessId(out uint pid);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6F5A3F8C6B5A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
    }

    // which process is playing: Windows names the media app (e.g. "Chrome", "MSEdge", "Spotify.exe")
    static string PlayingProcess()
    {
        var s = Session();
        if (s == null) return null;
        var id = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
        if (id.Contains("chrome")) return "chrome";
        if (id.Contains("edge")) return "msedge";
        if (id.Contains("firefox") || id.Contains("308046b0af4a39cb")) return "firefox";
        if (id.Contains("brave")) return "brave";
        if (id.Contains("opera")) return "opera";
        var name = id.Substring(id.LastIndexOf('!') + 1);
        return Path.GetFileNameWithoutExtension(name.Substring(name.LastIndexOf('\\') + 1));
    }

    // set (level >= 0) or read (level < 0) the volume of every audio session of the playing app; returns its volume or -1
    static float AppVolume(float level)
    {
        var proc = PlayingProcess();
        if (proc == null) return -1;
        IMMDevice dev;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0) return -1;
        var iid = typeof(IAudioSessionManager2).GUID;
        object o;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return -1;
        IAudioSessionEnumerator list;
        if (((IAudioSessionManager2)o).GetSessionEnumerator(out list) != 0) return -1;
        int n; list.GetCount(out n);
        float found = -1;
        var ctx = Guid.Empty;
        for (int k = 0; k < n; k++)
        {
            IAudioSessionControl2 sc;
            if (list.GetSession(k, out sc) != 0 || sc == null) continue;
            uint pid; sc.GetProcessId(out pid);
            string pname;
            try { pname = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { continue; }
            if (pname != proc) continue;
            var vol = (ISimpleAudioVolume)sc;
            if (level >= 0) vol.SetMasterVolume(level, ref ctx);
            float cur; if (vol.GetMasterVolume(out cur) == 0 && found < 0) found = cur;
        }
        return found;
    }

    static void Cmd(string c)
    {
        if (c == "mini/on" || c == "mini/off") { Mini(c == "mini/on"); return; }
        int pct;
        if (c.StartsWith("vol/") && int.TryParse(c.Substring(4), out pct)) { AppVolume(Math.Max(0, Math.Min(100, pct)) / 100f); return; }
        var s = Session();
        if (s == null) return;
        long sec;
        if (c == "toggle") Await(s.TryTogglePlayPauseAsync());
        else if (c == "next") Await(s.TrySkipNextAsync());
        else if (c == "prev") Await(s.TrySkipPreviousAsync());
        else if (c.StartsWith("seek/") && long.TryParse(c.Substring(5), out sec))
            Await(s.TryChangePlaybackPositionAsync(sec * 10000000));
        else if (c.StartsWith("repeat/") && long.TryParse(c.Substring(7), out sec)) // 0 off, 1 one song, 2 all
            Await(s.TryChangeAutoRepeatModeAsync((Windows.Media.MediaPlaybackAutoRepeatMode)(int)sec));
    }

    // waits for a Windows call (the usual helper needs a dev SDK that isn't on normal PCs)
    static T Await<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(5);
        if (op.Status != AsyncStatus.Completed) throw new Exception("Windows media call failed", op.ErrorCode);
        return op.GetResults();
    }

    // the page is built into the exe, so the exe works from any folder
    static byte[] Html()
    {
        using (var r = typeof(App).Assembly.GetManifestResourceStream("standby.html"))
        using (var ms = new MemoryStream())
        {
            // the page file has no <head> of its own (the website adds one), so add the basics here
            var head = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
            ms.Write(head, 0, head.Length);
            r.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static float VolSafe() { try { return AppVolume(-1); } catch { return -1; } }

    static byte[] Utf(string s) { return Encoding.UTF8.GetBytes(s); }

    static string Js(string s)
    {
        if (s == null) return "null";
        var b = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') b.Append('\\').Append(ch);
            else if (ch < 0x20) b.AppendFormat("\\u{0:x4}", (int)ch);
            else b.Append(ch);
        }
        return b.Append('"').ToString();
    }
}
