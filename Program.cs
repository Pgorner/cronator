using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Cronator
{
    internal static class Program
    {
        // ===== Win32 wallpaper SPI =====
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 0x0014;
        private const int SPI_GETDESKWALLPAPER = 0x0073;
        private const int SPIF_UPDATEINIFILE   = 0x0001;
        private const int SPIF_SENDCHANGE      = 0x0002;

        private static bool ApplyWallpaperAll(string path)
        {
            // Explorer sometimes needs a tiny nudge
            for (int i = 0; i < 2; i++)
            {
                if (SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                    return true;
                Thread.Sleep(80);
            }
            return false;
        }

        private static string GetCurrentWallpaperPath()
        {
            var buf = new string('\0', 260);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, buf.Length, buf, 0))
            {
                var p = buf.TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
            }

            try
            {
                var themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
                var trans  = Path.Combine(themes, "TranscodedWallpaper");
                if (File.Exists(trans)) return trans;
            }
            catch { }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
                var wall = key?.GetValue("WallPaper") as string;
                if (!string.IsNullOrWhiteSpace(wall) && File.Exists(wall)) return wall;
            }
            catch { }

            return "";
        }

        // ===== IDesktopWallpaper (per-monitor) =====
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            void GetMonitorDevicePathCount(out uint count);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
            void SetBackgroundColor(uint color);
            void GetBackgroundColor(out uint color);
            void SetPosition(DesktopWallpaperPosition position);
            void GetPosition(out DesktopWallpaperPosition position);
            void SetSlideshow(IntPtr items);
            void GetSlideshow(out IntPtr items);
            void SetSlideshowOptions(DesktopSlideshowOptions options, uint slideshowTick);
            void GetSlideshowOptions(out DesktopSlideshowOptions options, out uint slideshowTick);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, DesktopSlideshowDirection direction);
            void GetStatus(out DesktopSlideshowState state);
            void Enable(bool enable);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private enum DesktopWallpaperPosition { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }
        [Flags] private enum DesktopSlideshowOptions { None = 0, ShuffleImages = 0x01 }
        private enum DesktopSlideshowState { None = 0, Enabled = 0x01, Slideshow = 0x02, DisabledByRemoteSession = 0x04 }
        private enum DesktopSlideshowDirection { Forward = 0, Backward = 1 }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid clsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? ppv);
        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private static readonly Guid CLSID_DesktopWallpaper = new Guid(0xC2CF3110, 0x460E, 0x4FC1, 0xB9, 0xD0, 0x8A, 0x4F, 0x7F, 0x9A, 0xD3, 0x3C);
        private static readonly Guid IID_IDesktopWallpaper = new Guid(0xB92B56A9, 0x8B55, 0x4E14, 0x9A, 0x89, 0x01, 0x99, 0xBB, 0xB6, 0xF9, 0x3B);

        private static IDesktopWallpaper CreateDesktopWallpaper()
        {
            var clsid = CLSID_DesktopWallpaper; var iid = IID_IDesktopWallpaper;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out object? obj);
            if (hr != 0 || obj is null) throw new InvalidOperationException($"CoCreateInstance DesktopWallpaper failed: 0x{hr:X8}");
            return (IDesktopWallpaper)obj;
        }

        // ===== App state =====
        private static IDesktopWallpaper? _dw; // null => SPAN fallback
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "cronator_wall");

        // backup (global style + image)
        private static string? _backupWall;
        private static string? _backupWallLocal;
        private static int _backupStyle;
        private static int _backupTile;

        // selection
        private static string? _monId;
        private static int _monIndex = -1;

        private static CancellationTokenSource? _ticker;      // optional user timer
        private static CancellationTokenSource? _clockTicker; // second-aligned tick
        private static volatile bool _restored;
        private static readonly object _lock = new();

        // ===== Widgets =====
        private static readonly WidgetManager _widgets = new();

        // ===== Smooth composite cache (SPAN) =====
        private static readonly object _paintLock = new();
        private static Bitmap? _cachedBase;                // wallpaper base (virtual screen, no widgets)
        private static int _baseW, _baseH;                 // size of _cachedBase
        private static Rectangle _cachedVirt;              // virtual screen used when building base
        private static int _lastSecondPainted = -1;        // avoid redundant re-draws
        private static readonly string StableSpanPath = Path.Combine(TempDir, "cronator_span.bmp");
        private static bool _spanStyleSet;

        [STAThread]
        private static void Main()
        {
            Console.Title = "cronator — DLL widgets on wallpaper (COM if possible, SPAN otherwise)";
            Directory.CreateDirectory(TempDir);

            // Safety nets
            AppDomain.CurrentDomain.UnhandledException += (s, e) => TryRestore();
            TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); TryRestore(); };
            AppDomain.CurrentDomain.ProcessExit     += (s, e) => TryRestore();
            Console.CancelKeyPress                  += (s, e) => { e.Cancel = true; TryRestore(); Environment.Exit(0); };

            // Backup current wallpaper/style and copy bytes locally
            BackupCurrent();

            // Try COM
            try { _dw = CreateDesktopWallpaper(); Console.WriteLine("Mode: IDesktopWallpaper (per-monitor)."); }
            catch (Exception ex) { _dw = null; Console.WriteLine($"Mode: SPAN fallback (reason: {ex.Message})"); }

            // Help
            Console.WriteLine("Commands:");
            Console.WriteLine("  monitors              list monitors");
            Console.WriteLine("  use <index>           select a monitor");
            Console.WriteLine("  u                     update selected monitor once");
            Console.WriteLine("  t <sec>               auto-update every N seconds");
            Console.WriteLine("  s                     stop timer");
            Console.WriteLine("  q                     restore & quit");
            Console.WriteLine();

            // Default selection + load widgets + first paint
            if (_dw != null)
            {
                ListMonitorsCOM(out var ids, out var rects);
                if (ids.Count > 0)
                {
                    SelectMonitorCOM(0, ids, rects);
                    LoadWidgetsForCurrentMonitor(rects[0]);
                    UpdateSelectedCOM(ids, rects);
                }
            }
            else
            {
                var screens = System.Windows.Forms.Screen.AllScreens.ToList();
                ListMonitorsSPAN(screens);
                if (screens.Count > 0)
                {
                    SelectMonitorSPAN(0, screens);
                    LoadWidgetsForCurrentMonitor(screens[0].Bounds);

                    // Build base and point Explorer once at stable file
                    RebuildBaseComposite(screens);
                    ApplySpanStyleSetOnce();
                    UpdateSelectedSPAN(screens, initialApply: true);
                }
            }

            // Second-aligned widget tick
            StartClockTimer();

            // Tray (embedded assets)
            Tray.StartFromEmbedded(
                icoResource: "cronator.assets.logo.ico",
                gifResource: "cronator.assets.logo.gif",
                gifFps: 8
            );

            // Console loop
            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line == null) break;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var cmd = parts[0].ToLowerInvariant();
                try
                {
                    if (cmd is "q" or "quit" or "exit")
                    {
                        StopTimer();
                        StopClockTimer();
                        TryRestore();
                        Console.WriteLine("Restored. Bye.");
                        break;
                    }
                    else if (cmd == "monitors")
                    {
                        if (_dw != null) ListMonitorsCOM(out _, out _);
                        else ListMonitorsSPAN(System.Windows.Forms.Screen.AllScreens.ToList());
                    }
                    else if (cmd == "use" && parts.Length >= 2 && int.TryParse(parts[1], out var idx))
                    {
                        if (_dw != null)
                        {
                            ListMonitorsCOM(out var ids, out var rects);
                            SelectMonitorCOM(idx, ids, rects);
                            LoadWidgetsForCurrentMonitor(rects[Math.Max(0, Math.Min(idx, rects.Count - 1))]);
                            UpdateSelectedCOM(ids, rects);
                        }
                        else
                        {
                            var screens = System.Windows.Forms.Screen.AllScreens.ToList();
                            SelectMonitorSPAN(idx, screens);
                            LoadWidgetsForCurrentMonitor(screens[Math.Max(0, Math.Min(idx, screens.Count - 1))].Bounds);

                            // Rebuild base when selection/layout changes
                            RebuildBaseComposite(screens);
                            UpdateSelectedSPAN(screens, initialApply: false);
                        }
                    }
                    else if (cmd == "u")
                    {
                        if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens, initialApply: false); }
                    }
                    else if (cmd == "t" && parts.Length >= 2 && int.TryParse(parts[1], out var sec) && sec > 0)
                    {
                        StartTimer(sec);
                    }
                    else if (cmd == "s")
                    {
                        StopTimer();
                    }
                    else
                    {
                        Console.WriteLine("Commands: monitors | use <index> | u | t <sec> | s | q");
                    }
                }
                catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); }
            }
        }

        // ===== Backup / Restore =====
        private static void BackupCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
                _backupWall  = key?.GetValue("WallPaper") as string;
                _backupStyle = Convert.ToInt32(key?.GetValue("WallpaperStyle") ?? 10);
                _backupTile  = Convert.ToInt32(key?.GetValue("TileWallpaper") ?? 0);
            }
            catch { }

            try
            {
                Directory.CreateDirectory(TempDir);
                var src = GetCurrentWallpaperPath();
                if (!string.IsNullOrWhiteSpace(src) && File.Exists(src))
                {
                    var ext = Path.GetExtension(src);
                    if (string.IsNullOrEmpty(ext)) ext = ".jpg"; // Transcoded has no extension
                    _backupWallLocal = Path.Combine(TempDir, "cronator_backup" + ext);
                    File.Copy(src, _backupWallLocal, overwrite: true);
                }
            }
            catch { _backupWallLocal = null; }
        }

        private static void TryRestore()
        {
            lock (_lock)
            {
                if (_restored) return;
                _restored = true;

                try { StopTimer(); } catch { }
                try { StopClockTimer(); } catch { }

                try
                {
                    // Dispose cache
                    lock (_paintLock)
                    {
                        _cachedBase?.Dispose(); _cachedBase = null;
                    }

                    if (!string.IsNullOrWhiteSpace(_backupWallLocal) && File.Exists(_backupWallLocal))
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                        key?.SetValue("WallpaperStyle", _backupStyle.ToString(), RegistryValueKind.String);
                        key?.SetValue("TileWallpaper", _backupTile.ToString(), RegistryValueKind.String);
                        ApplyWallpaperAll(_backupWallLocal);
                        Console.WriteLine("Wallpaper restored (from local backup).");
                    }
                    else if (!string.IsNullOrWhiteSpace(_backupWall) && File.Exists(_backupWall))
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                        key?.SetValue("WallpaperStyle", _backupStyle.ToString(), RegistryValueKind.String);
                        key?.SetValue("TileWallpaper", _backupTile.ToString(), RegistryValueKind.String);
                        ApplyWallpaperAll(_backupWall);
                        Console.WriteLine("Wallpaper restored (from registry path).");
                    }
                    else
                    {
                        Console.WriteLine("No wallpaper backup found; leaving current image.");
                    }

                    // cleanup temp files
                    if (Directory.Exists(TempDir))
                    {
                        foreach (var f in Directory.GetFiles(TempDir))
                            try { File.Delete(f); } catch { }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Restore error: " + ex.Message); }
            }
        }

        // ===== Optional user timer =====
        private static void StartTimer(int sec)
        {
            StopTimer();
            Console.WriteLine($"Auto-update every {sec}s. (type S to stop)");
            _ticker = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!_ticker!.IsCancellationRequested)
                {
                    try
                    {
                        if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens, initialApply: false); }
                    }
                    catch { }
                    try { await Task.Delay(TimeSpan.FromSeconds(sec), _ticker.Token); }
                    catch { break; }
                }
            });
        }
        private static void StopTimer()
        {
            if (_ticker != null) { _ticker.Cancel(); _ticker.Dispose(); _ticker = null; Console.WriteLine("Timer stopped."); }
        }

        // ===== Second-aligned widget tick =====
        private static void StartClockTimer()
        {
            StopClockTimer();
            _clockTicker = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                var token = _clockTicker!.Token;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Align to next second boundary
                        var now = DateTime.Now;
                        int msToNextSecond = 1000 - now.Millisecond;
                        if (msToNextSecond < 1) msToNextSecond = 1;
                        await Task.Delay(msToNextSecond, token);

                        int sec = DateTime.Now.Second;
                        if (sec == _lastSecondPainted) continue;
                        _lastSecondPainted = sec;

                        if (_dw != null)
                        {
                            ListMonitorsCOM(out var ids, out var rects);
                            UpdateSelectedCOM(ids, rects);
                        }
                        else
                        {
                            var screens = System.Windows.Forms.Screen.AllScreens.ToList();

                            // Rebuild base if virtual screen changed
                            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
                            if (_cachedBase == null || _baseW != virt.Width || _baseH != virt.Height)
                            {
                                RebuildBaseComposite(screens);
                                UpdateSelectedSPAN(screens, initialApply: true); // re-apply once if size changed
                            }
                            else
                            {
                                UpdateSelectedSPAN(screens, initialApply: false);
                            }
                        }
                    }
                    catch (TaskCanceledException) { break; }
                    catch { /* ignore one blip */ }
                }
            });
        }
        private static void StopClockTimer()
        {
            if (_clockTicker != null)
            {
                _clockTicker.Cancel();
                _clockTicker.Dispose();
                _clockTicker = null;
                Console.WriteLine("Clock timer stopped.");
            }
        }

        // ===== COM path =====
        private static void ListMonitorsCOM(out List<string> ids, out List<RECT> rects)
        {
            ids = new(); rects = new();
            _dw!.GetMonitorDevicePathCount(out uint count);
            Console.WriteLine($"Monitors found (COM): {count}");
            for (uint i = 0; i < count; i++)
            {
                string id = _dw.GetMonitorDevicePathAt(i);
                _dw.GetMonitorRECT(id, out RECT r);
                ids.Add(id); rects.Add(r);
                Console.WriteLine($"  [{i}] {id}  rect=({r.Left},{r.Top})–({r.Right},{r.Bottom}) size={r.Right - r.Left}x{r.Bottom - r.Top}");
                try { var cur = _dw.GetWallpaper(id); Console.WriteLine($"      current='{cur}'"); }
                catch { Console.WriteLine("      current=<unknown>"); }
            }
            Console.WriteLine();
        }

        private static void SelectMonitorCOM(int index, List<string> ids, List<RECT> rects)
        {
            if (index < 0 || index >= ids.Count) { Console.WriteLine("Invalid index."); return; }
            _monId = ids[index];
            _monIndex = index;
            var r = rects[index];
            Console.WriteLine($"Selected monitor [{index}] {_monId}");
            Console.WriteLine($"  size={r.Right - r.Left}x{r.Bottom - r.Top}, rect=({r.Left},{r.Top})–({r.Right},{r.Bottom})");
        }

        private static void UpdateSelectedCOM(List<string> ids, List<RECT> rects)
        {
            if (_monId == null) { Console.WriteLine("No monitor selected."); return; }
            int idx = _monIndex;
            if (idx < 0 || idx >= ids.Count || ids[idx] != _monId) { Console.WriteLine("Selection out of date; re-select."); return; }

            var r = rects[idx];
            int w = Math.Max(1, r.Right - r.Left);
            int h = Math.Max(1, r.Bottom - r.Top);

            var outBmp = Path.Combine(TempDir, $"cronator_{idx}.bmp");
            try
            {
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    _widgets.DrawAll(g, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), DateTime.Now);
                }
                bmp.Save(outBmp, ImageFormat.Bmp);
                _dw!.SetPosition(DesktopWallpaperPosition.Fill);
                _dw.SetWallpaper(_monId, outBmp);
                Console.WriteLine($"Applied per-monitor wallpaper: monitor[{idx}] -> {outBmp}");
            }
            catch (Exception ex) { Console.WriteLine("Update error: " + ex.Message); }
        }

        // ===== SPAN path =====
        private static void ListMonitorsSPAN(List<System.Windows.Forms.Screen> screens)
        {
            Console.WriteLine($"Monitors found (SPAN): {screens.Count}");
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            Console.WriteLine($"  Virtual: ({vs.Left},{vs.Top})–({vs.Right},{vs.Bottom}) size={vs.Width}x{vs.Height}");
            for (int i = 0; i < screens.Count; i++)
            {
                var b = screens[i].Bounds;
                Console.WriteLine($"  [{i}] rect=({b.Left},{b.Top})–({b.Right},{b.Bottom}) size={b.Width}x{b.Height} primary={screens[i].Primary}");
            }
            Console.WriteLine();
        }

        private static void SelectMonitorSPAN(int index, List<System.Windows.Forms.Screen> screens)
        {
            if (index < 0 || index >= screens.Count) { Console.WriteLine("Invalid index."); return; }
            _monIndex = index;
            var b = screens[index].Bounds;
            Console.WriteLine($"Selected monitor [{index}] (SPAN mode)");
            Console.WriteLine($"  size={b.Width}x{b.Height}, rect=({b.Left},{b.Top})–({b.Right},{b.Bottom})");

            // If selection changes, rebuild base so first tick is instant
            RebuildBaseComposite(screens);
        }

        private static void ApplySpanStyleSetOnce()
        {
            if (_spanStyleSet) return;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                key?.SetValue("WallpaperStyle", "22", RegistryValueKind.String); // Span
                key?.SetValue("TileWallpaper",  "0",  RegistryValueKind.String);
                _spanStyleSet = true;
            }
            catch { }
        }

        private static void RebuildBaseComposite(List<System.Windows.Forms.Screen> screens)
        {
            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
            int W = virt.Width, H = virt.Height;

            // Compose from original wallpaper snapshot if available
            Image? baseWall = null;
            if (!string.IsNullOrWhiteSpace(_backupWallLocal) && File.Exists(_backupWallLocal))
                baseWall = LoadImageUnlocked(_backupWallLocal);
            else if (!string.IsNullOrWhiteSpace(_backupWall) && File.Exists(_backupWall))
                baseWall = LoadImageUnlocked(_backupWall);

            lock (_paintLock)
            {
                _cachedBase?.Dispose();
                _cachedBase = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
                _baseW = W; _baseH = H; _cachedVirt = virt;

                using (var g = Graphics.FromImage(_cachedBase))
                {
                    g.SmoothingMode      = SmoothingMode.HighQuality;
                    g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.CompositingMode    = CompositingMode.SourceOver;
                    g.TextRenderingHint  = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    if (baseWall != null)
                    {
                        foreach (var scr in screens)
                            DrawFillImage(g, baseWall, scr.Bounds, virt);
                    }
                    else
                    {
                        g.Clear(Color.Black);
                    }
                }
                _lastSecondPainted = -1; // force next tick to repaint
            }

            baseWall?.Dispose();
        }

        private static void UpdateSelectedSPAN(List<System.Windows.Forms.Screen> screens, bool initialApply)
        {
            if (_monIndex < 0 || _monIndex >= screens.Count) { Console.WriteLine("No monitor selected."); return; }

            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
            // Rebuild base if needed (virtual size changes)
            if (_cachedBase == null || _baseW != virt.Width || _baseH != virt.Height)
                RebuildBaseComposite(screens);

            string tmp = StableSpanPath + ".tmp";
            try
            {
                lock (_paintLock)
                {
                    using (var frame = new Bitmap(_baseW, _baseH, PixelFormat.Format32bppPArgb))
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.SmoothingMode      = SmoothingMode.HighQuality;
                        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.CompositingMode    = CompositingMode.SourceOver;
                        g.TextRenderingHint  = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                        g.DrawImageUnscaled(_cachedBase!, 0, 0);

                        var selected = screens[_monIndex].Bounds;
                        _widgets.DrawAll(g, selected, _cachedVirt, DateTime.Now);

                        if (!SaveBmpWithRetry(frame, tmp))
                        {
                            Console.WriteLine("SPAN update error: failed to save frame.");
                            return;
                        }
                    }

                    // Atomic replace
                    try
                    {
                        if (!File.Exists(StableSpanPath)) File.Move(tmp, StableSpanPath);
                        else File.Replace(tmp, StableSpanPath, null, true);
                    }
                    catch
                    {
                        try
                        {
                            if (File.Exists(StableSpanPath)) File.Delete(StableSpanPath);
                            File.Move(tmp, StableSpanPath);
                        }
                        catch { /* last resort */ }
                    }
                }

                // Always poke Explorer so it repaints even though the path stayed the same.
                ApplySpanStyleSetOnce();
                if (!ApplyWallpaperAll(StableSpanPath))
                    Console.WriteLine($"Failed to set wallpaper (err={Marshal.GetLastWin32Error()}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine("SPAN update error: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }


        // ===== Drawing helpers =====
        private static Image? LoadImageUnlocked(string path)
        {
            try
            {
                using var fs  = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
                return new Bitmap(img); // clone
            }
            catch { return null; }
        }

        private static bool SaveBmpWithRetry(Bitmap bmp, string path, int retries = 3, int delayMs = 30)
        {
            for (int i = 0; i < retries; i++)
            {
                try { bmp.Save(path, ImageFormat.Bmp); return true; }
                catch { Thread.Sleep(delayMs); }
            }
            return false;
        }

        private static void DrawFillImage(Graphics g, Image img, Rectangle monitorRect, Rectangle virt)
        {
            float rw = (float)monitorRect.Width / img.Width;
            float rh = (float)monitorRect.Height / img.Height;
            float scale = Math.Max(rw, rh);
            int dw = (int)(img.Width * scale);
            int dh = (int)(img.Height * scale);
            int dx = (monitorRect.Left - virt.Left) + (monitorRect.Width - dw) / 2;
            int dy = (monitorRect.Top  - virt.Top ) + (monitorRect.Height - dh) / 2;
            g.DrawImage(img, new Rectangle(dx, dy, dw, dh), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
        }

        // ===== Tray hooks (minimal) =====
        internal static void TrayExitRequested()
        {
            StopTimer();
            StopClockTimer();
            TryRestore();
            Cronator.Tray.Stop();
            Environment.Exit(0);
        }

        internal static string[] TrayGetMonitorList()
        {
            var screens = System.Windows.Forms.Screen.AllScreens.ToList();
            return screens.Select(s => $"{s.Bounds.Width}x{s.Bounds.Height}" + (s.Primary ? " (primary)" : "")).ToArray();
        }

        internal static int TrayGetSelectedMonitorIndex() => _monIndex < 0 ? 0 : _monIndex;

        internal static void TraySelectMonitor(int index)
        {
            if (_dw != null)
            {
                ListMonitorsCOM(out var ids, out var rects);
                SelectMonitorCOM(Math.Max(0, Math.Min(index, ids.Count - 1)), ids, rects);
            }
            else
            {
                var screens = System.Windows.Forms.Screen.AllScreens.ToList();
                SelectMonitorSPAN(Math.Max(0, Math.Min(index, screens.Count - 1)), screens);
                RebuildBaseComposite(screens);
                UpdateSelectedSPAN(screens, initialApply: true);
            }
        }

        internal static void TraySetTimer(int? seconds)
        {
            if (seconds.HasValue && seconds.Value > 0) StartTimer(seconds.Value);
            else StopTimer();
        }

        internal static void TrayUpdateOnce()
        {
            if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
            else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens, initialApply: false); }
        }

        // ===== Widget loader/manager =====
        private interface IHostWidget
        {
            string Name { get; }
            void Draw(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now);
        }

        private sealed class ReflectedWidgetAdapter : IHostWidget
        {
            private readonly object _impl;
            private readonly MethodInfo _draw;
            private readonly MethodInfo? _apply;
            public string Name { get; }

            public ReflectedWidgetAdapter(object impl, string name)
            {
                _impl = impl;
                Name = name;
                _draw  = impl.GetType().GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance)
                          ?? throw new InvalidOperationException("Widget DLL lacks public Draw(Graphics, Rectangle, Rectangle, DateTime).");
                _apply = impl.GetType().GetMethod("ApplySettings", BindingFlags.Public | BindingFlags.Instance);
            }

            public void ApplySettings(Dictionary<string, object?> settings) => _apply?.Invoke(_impl, new object?[] { settings });
            public void Draw(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now) =>
                _draw.Invoke(_impl, new object?[] { g, monitorRect, virt, now });
        }

        private sealed class WidgetManifest
        {
            public string? name         { get; set; }
            public string? displayName  { get; set; }
            public string? version      { get; set; }
            public bool    enabled      { get; set; } = false;
            public string? kind         { get; set; }   // "dll"
            public string? assembly     { get; set; }   // "ClockWidget.dll"
            public string? type         { get; set; }   // "ClockWidgetNS.ClockWidget"
            public Dictionary<string, object?>? settings { get; set; }
        }

        private sealed class WidgetUserConfig
        {
            public bool? enabled { get; set; }
            public Dictionary<string, object?>? settings { get; set; }
        }

        private sealed class WidgetManager
        {
            private readonly List<IHostWidget> _widgets = new();
            public IReadOnlyList<IHostWidget> Widgets => _widgets;

            public void LoadFromFolder(string root, Rectangle selectedMonitor, Rectangle virt)
            {
                _widgets.Clear();

                if (!Directory.Exists(root))
                {
                    Console.WriteLine($"[Widgets] root folder not found: {root}");
                    return;
                }

                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(folder);
                    try
                    {
                        var manifestPath = Path.Combine(folder, "manifest.json");
                        if (!File.Exists(manifestPath))
                        {
                            Console.WriteLine($"[Widgets] {name}: missing manifest.json → skipped.");
                            continue;
                        }

                        var manifest = JsonSerializer.Deserialize<WidgetManifest>(File.ReadAllText(manifestPath))
                                    ?? new WidgetManifest();

                        // Merge user config if present
                        var userPath = Path.Combine(folder, "config.user.json");
                        WidgetUserConfig? user = File.Exists(userPath)
                            ? JsonSerializer.Deserialize<WidgetUserConfig>(File.ReadAllText(userPath))
                            : null;

                        bool enabled = user?.enabled ?? manifest.enabled;
                        if (!enabled)
                        {
                            Console.WriteLine($"[Widgets] {name}: disabled.");
                            continue;
                        }

                        // Effective settings = defaults + user overrides
                        var effective = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        if (manifest.settings != null)
                            foreach (var kv in manifest.settings) effective[kv.Key] = kv.Value;
                        if (user?.settings != null)
                            foreach (var kv in user.settings) effective[kv.Key] = kv.Value;

                        if (!"dll".Equals(manifest.kind, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[Widgets] {name}: unsupported kind '{manifest.kind}'.");
                            continue;
                        }

                        var asmPath  = manifest.assembly;
                        var typeName = manifest.type;
                        if (string.IsNullOrWhiteSpace(asmPath))
                        {
                            Console.WriteLine($"[Widgets] {name}: 'assembly' missing → skipped.");
                            continue;
                        }

                        if (!TryResolveAssemblyPath(folder, asmPath, out var fullAsm))
                        {
                            Console.WriteLine($"[Widgets] {name}: assembly not found: {Path.Combine(folder, asmPath)}");
                            continue;
                        }

                        var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(fullAsm);

                        // Try the declared type first
                        Type? t = null;
                        if (!string.IsNullOrWhiteSpace(typeName))
                        {
                            t = asm.GetType(typeName, throwOnError: false, ignoreCase: false);
                            if (t == null)
                            {
                                Console.WriteLine($"[Widgets] {name}: type not found in '{Path.GetFileName(fullAsm)}': {typeName}");
                                // Log what IS available
                                try
                                {
                                    var exported = asm.GetExportedTypes().Select(x => x.FullName).OrderBy(x => x).ToArray();
                                    if (exported.Length > 0)
                                    {
                                        Console.WriteLine($"[Widgets] {name}: exported types:");
                                        foreach (var tt in exported) Console.WriteLine($"           - {tt}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Widgets] {name}: no exported public types.");
                                    }
                                }
                                catch { }
                            }
                        }

                        // Auto-detect a widget type if none matched
                        if (t == null)
                        {
                            t = AutoDetectWidgetType(asm);
                            if (t != null)
                            {
                                Console.WriteLine($"[Widgets] {name}: auto-detected widget type '{t.FullName}'.");
                            }
                            else
                            {
                                Console.WriteLine($"[Widgets] {name}: could not auto-detect a widget class → skipped.");
                                continue;
                            }
                        }

                        var inst = Activator.CreateInstance(t)
                                ?? throw new InvalidOperationException("Could not construct widget type.");

                        string widgetName = manifest.name ?? name;
                        var adapter = new ReflectedWidgetAdapter(inst, widgetName);
                        adapter.ApplySettings(effective);
                        _widgets.Add(adapter);

                        Console.WriteLine($"[Widgets] loaded: {widgetName} ({Path.GetFileName(fullAsm)})");
                    }
                    catch (BadImageFormatException bif)
                    {
                        Console.WriteLine($"[Widgets] {name} load error (BadImageFormat): {bif.Message}");
                    }
                    catch (FileLoadException fle)
                    {
                        Console.WriteLine($"[Widgets] {name} load error (FileLoad): {fle.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Widgets] {name} load error: {ex.Message}");
                    }
                }

                if (_widgets.Count == 0) Console.WriteLine("[Widgets] none enabled.");
            }

            public void DrawAll(Graphics g, Rectangle monitorRect, Rectangle virt, DateTime now)
            {
                foreach (var w in _widgets)
                {
                    try { w.Draw(g, monitorRect, virt, now); }
                    catch (Exception ex) { Console.WriteLine($"[Widgets] draw '{w.Name}' error: {ex.Message}"); }
                }
            }

            // ---- helpers ----
            private static Type? AutoDetectWidgetType(Assembly asm)
            {
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.IsAbstract || !t.IsClass) continue;
                        var draw = t.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                        if (draw == null) continue;

                        var ps = draw.GetParameters();
                        if (ps.Length != 4) continue;
                        if (ps[0].ParameterType != typeof(Graphics)) continue;
                        if (ps[1].ParameterType != typeof(Rectangle)) continue;
                        if (ps[2].ParameterType != typeof(Rectangle)) continue;
                        if (ps[3].ParameterType != typeof(DateTime)) continue;

                        return t; // first suitable type
                    }
                }
                catch { }
                return null;
            }

            private static bool TryResolveAssemblyPath(string folder, string asmRelativeOrName, out string fullPath)
            {
                var candidate = Path.Combine(folder, asmRelativeOrName);
                if (File.Exists(candidate)) { fullPath = candidate; return true; }

                var fileName = Path.GetFileName(asmRelativeOrName);
                var found = Directory.GetFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) { fullPath = found; return true; }

                var anyDll = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories)
                                      .OrderByDescending(File.GetLastWriteTimeUtc)
                                      .FirstOrDefault();
                if (!string.IsNullOrEmpty(anyDll))
                {
                    Console.WriteLine($"[Widgets] {Path.GetFileName(folder)}: using discovered DLL '{Path.GetFileName(anyDll)}'.");
                    fullPath = anyDll;
                    return true;
                }

                fullPath = string.Empty;
                return false;
            }
        }

        // ===== Helper to reload widgets for current monitor =====
        private static void LoadWidgetsForCurrentMonitor(RECT r)
        {
            var sel = new Rectangle(0, 0, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            var vs  = System.Windows.Forms.SystemInformation.VirtualScreen;
            string widgetsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "widgets");
            _widgets.LoadFromFolder(widgetsRoot, sel, vs);
        }

        private static void LoadWidgetsForCurrentMonitor(Rectangle monitorBounds)
        {
            var vs  = System.Windows.Forms.SystemInformation.VirtualScreen;
            string widgetsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "widgets");
            _widgets.LoadFromFolder(widgetsRoot, monitorBounds, vs);
        }
    }
}
