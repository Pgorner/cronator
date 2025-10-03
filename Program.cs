using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Linq;

namespace Cronator
{
    internal static class Program
    {
        // ===== Win32 wallpaper SPI =====
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 0x0014;
        private const int SPI_GETDESKWALLPAPER = 0x0073;
        private const int SPIF_UPDATEINIFILE = 0x0001;
        private const int SPIF_SENDCHANGE = 0x0002;

        private static bool ApplyWallpaperAll(string path)
        {
            for (int i = 0; i < 2; i++)
            {
                if (SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                    return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static string GetCurrentWallpaperPath()
        {
            // SPI_GETDESKWALLPAPER returns a path (buffer size up to MAX_PATH).
            var buf = new string('\0', 260);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, buf.Length, buf, 0))
            {
                var p = buf.TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
            }

            // Fallback: Themes\TranscodedWallpaper (no extension)
            try
            {
                var themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
                var trans = Path.Combine(themes, "TranscodedWallpaper");
                if (File.Exists(trans)) return trans;
            }
            catch { }

            // Registry fallback
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
        private static readonly Guid CLSID_DesktopWallpaper = new Guid(
            0xC2CF3110, 0x460E, 0x4FC1, 0xB9, 0xD0, 0x8A, 0x4F, 0x7F, 0x9A, 0xD3, 0x3C);
        private static readonly Guid IID_IDesktopWallpaper = new Guid(
            0xB92B56A9, 0x8B55, 0x4E14, 0x9A, 0x89, 0x01, 0x99, 0xBB, 0xB6, 0xF9, 0x3B);

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
        private static readonly string TempSpan = Path.Combine(TempDir, "cronator_span.bmp");
        private static string? _lastSpanBase; // remember last composite base so we don't blank other monitors

        // backup (global style + image)
        private static string? _backupWall;

        private static string? _backupWallLocal; // temp copy of actual wallpaper bytes to restore from

        private static int _backupStyle;        // WallpaperStyle
        private static int _backupTile;         // TileWallpaper

        // selection
        private static string? _monId;
        private static int _monIndex = -1;

        private static CancellationTokenSource? _ticker;
        private static volatile bool _restored;
        private static readonly object _lock = new();

        // overlay color state
        private static readonly Color[] _palette = new[]
        {
            Color.FromArgb(160, 255, 59, 48),   // red-ish
            Color.FromArgb(160, 255, 159, 10),  // orange
            Color.FromArgb(160, 255, 214, 10),  // yellow
            Color.FromArgb(160, 52, 199, 89),   // green
            Color.FromArgb(160, 0, 122, 255),   // blue
            Color.FromArgb(160, 175, 82, 222),  // purple
            Color.FromArgb(160, 90, 200, 250),  // light blue
        };
        private static int _paletteIdx = 0;
        private static bool _randomColors = false;

        [STAThread]
        private static void Main()
        {
            Console.Title = "cronator — per-monitor (COM if possible, SPAN otherwise)";
            Directory.CreateDirectory(TempDir);

            // Safety nets
            AppDomain.CurrentDomain.UnhandledException += (s, e) => TryRestore();
            TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); TryRestore(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => TryRestore();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; TryRestore(); Environment.Exit(0); };

            // Backup
            BackupCurrent();

            // Try COM
            try { _dw = CreateDesktopWallpaper(); Console.WriteLine("Mode: IDesktopWallpaper (per-monitor)."); }
            catch (Exception ex) { _dw = null; Console.WriteLine($"Mode: SPAN fallback (reason: {ex.Message})"); }

            // Help
            Console.WriteLine("Commands:");
            Console.WriteLine("  monitors              list monitors");
            Console.WriteLine("  use <index>           select a monitor");
            Console.WriteLine("  u                     update selected monitor once");
            Console.WriteLine("  t <sec>               auto-update every N seconds (cycles overlay color)");
            Console.WriteLine("  s                     stop timer");
            Console.WriteLine("  color <name|random>   set overlay color (red|green|blue|yellow|purple|orange|lightblue|random)");
            Console.WriteLine("  q                     restore & quit");
            Console.WriteLine();

            // Default selection + paint
            if (_dw != null)
            {
                ListMonitorsCOM(out var ids, out var rects);
                if (ids.Count > 0) { SelectMonitorCOM(0, ids, rects); UpdateSelectedCOM(ids, rects); }
            }
            else
            {
                var screens = System.Windows.Forms.Screen.AllScreens.ToList();
                ListMonitorsSPAN(screens);
                if (screens.Count > 0) { SelectMonitorSPAN(0, screens); UpdateSelectedSPAN(screens); }
            }
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
                        if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); SelectMonitorCOM(idx, ids, rects); }
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); SelectMonitorSPAN(idx, screens); }
                    }
                    else if (cmd == "u")
                    {
                        if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens); }
                    }
                    else if (cmd == "t" && parts.Length >= 2 && int.TryParse(parts[1], out var sec) && sec > 0)
                    {
                        StartTimer(sec);
                    }
                    else if (cmd == "s")
                    {
                        StopTimer();
                    }
                    else if (cmd == "color" && parts.Length >= 2)
                    {
                        SetColor(parts[1]);
                        Console.WriteLine($"Color mode: {(_randomColors ? "random" : _palette[_paletteIdx].ToString())}");
                        // do a visible update
                        if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens); }
                    }
                    else
                    {
                        Console.WriteLine("Commands: monitors | use <index> | u | t <sec> | s | color <name|random> | q");
                    }
                }
                catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); }
            }
        }

        // ===== Color control =====
        private static void SetColor(string arg)
        {
            _randomColors = false;
            switch (arg.ToLowerInvariant())
            {
                case "red": _paletteIdx = 0; break;
                case "orange": _paletteIdx = 1; break;
                case "yellow": _paletteIdx = 2; break;
                case "green": _paletteIdx = 3; break;
                case "blue": _paletteIdx = 4; break;
                case "purple": _paletteIdx = 5; break;
                case "lightblue": _paletteIdx = 6; break;
                case "random": _randomColors = true; break;
                default: Console.WriteLine("Unknown color. Try: red, green, blue, yellow, purple, orange, lightblue, random"); break;
            }
        }
        private static Color NextColor()
        {
            if (_randomColors)
            {
                var r = new Random(unchecked(Environment.TickCount * 37 + _paletteIdx));
                return Color.FromArgb(160, r.Next(30, 255), r.Next(30, 255), r.Next(30, 255));
            }
            _paletteIdx = (_paletteIdx + 1) % _palette.Length;
            return _palette[_paletteIdx];
        }

        // ===== Backup / Restore =====
        private static void BackupCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
                _backupWall = key?.GetValue("WallPaper") as string;
                _backupStyle = Convert.ToInt32(key?.GetValue("WallpaperStyle") ?? 10);
                _backupTile = Convert.ToInt32(key?.GetValue("TileWallpaper") ?? 0);
            }
            catch { }

            try
            {
                Directory.CreateDirectory(TempDir);
                var src = GetCurrentWallpaperPath(); // resolves TranscodedWallpaper too
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

                try
                {
                    // Restore global wallpaper + style from our backup copy if available
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
                        using var key = Registry.CurrentUser.OpenSubKey(@" Control Panel\\Desktop", true);
                        key?.SetValue("WallpaperStyle", _backupStyle.ToString(), RegistryValueKind.String);
                        key?.SetValue("TileWallpaper", _backupTile.ToString(), RegistryValueKind.String);
                        ApplyWallpaperAll(_backupWall);
                        Console.WriteLine("Wallpaper restored (from registry path).");
                    }
                    else
                    {
                        Console.WriteLine("No wallpaper backup found; leaving current image.");
                    }


                    // Cleanup temp
                    if (Directory.Exists(TempDir))
                    {
                        foreach (var f in Directory.GetFiles(TempDir, "*.bmp"))
                            try { File.Delete(f); } catch { }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Restore error: " + ex.Message); }
            }
        }

        // ===== Timer =====
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
                        else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens, cycleColor: true); }
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

        // ===== COM path (per-monitor API) =====
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
                    DrawOverlay(g, new Rectangle(0, 0, w, h), idx); // just draw overlay in COM mode
                }
                bmp.Save(outBmp, ImageFormat.Bmp);
                _dw!.SetPosition(DesktopWallpaperPosition.Fill);
                _dw.SetWallpaper(_monId, outBmp);
                Console.WriteLine($"Applied per-monitor wallpaper: monitor[{idx}] -> {outBmp}");
            }
            catch (Exception ex) { Console.WriteLine("Update error: " + ex.Message); }
        }

        // ===== SPAN fallback path =====
        private static void ListMonitorsSPAN(List<System.Windows.Forms.Screen> screens)
        {
            Console.WriteLine($"Monitors found (SPAN): {screens.Count}");
            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
            Console.WriteLine($"  Virtual: ({virt.Left},{virt.Top})–({virt.Right},{virt.Bottom}) size={virt.Width}x{virt.Height}");
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
        }


        private static void UpdateSelectedSPAN(List<System.Windows.Forms.Screen> screens, bool cycleColor = false)
        {
            if (_monIndex < 0 || _monIndex >= screens.Count) { Console.WriteLine("No monitor selected."); return; }

            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
            int W = virt.Width, H = virt.Height;
            var selected = screens[_monIndex].Bounds;

            // Always start from the ORIGINAL wallpaper we backed up at launch
            Image? baseWall = null;
            if (!string.IsNullOrWhiteSpace(_backupWallLocal) && File.Exists(_backupWallLocal))
                baseWall = LoadImageUnlocked(_backupWallLocal);
            else if (!string.IsNullOrWhiteSpace(_backupWall) && File.Exists(_backupWall))
                baseWall = LoadImageUnlocked(_backupWall);
            // else -> null: we'll paint a solid background

            // choose overlay color
            Color overlay = cycleColor ? NextColor() : _palette[_paletteIdx];

            string outPath = Path.Combine(TempDir, $"cronator_span_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bmp");
            try
            {
                using var big = new Bitmap(W, H);
                using (var g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                    // Fresh base every tick (no reuse of previous composite)
                    if (baseWall != null)
                    {
                        foreach (var scr in screens)
                            DrawFillImage(g, baseWall, scr.Bounds, virt);
                    }
                    else
                    {
                        g.Clear(Color.Black); // or DimGray
                    }

                    // Overlay only on the selected monitor
                    DrawOverlay(g, selected, monitorIndex: _monIndex, overlayColor: overlay, virt: virt);
                }

                if (!SaveBmpWithRetry(big, outPath))
                {
                    Console.WriteLine("SPAN update error: failed to save composite.");
                    return;
                }

                // Apply as Span
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    key?.SetValue("WallpaperStyle", "22", RegistryValueKind.String); // Span
                    key?.SetValue("TileWallpaper", "0", RegistryValueKind.String);
                }

                if (!ApplyWallpaperAll(outPath))
                {
                    Console.WriteLine($"Failed to set wallpaper (err={Marshal.GetLastWin32Error()}).");
                }
                else
                {
                    Console.WriteLine($"Applied SPAN wallpaper: {outPath}");
                    // no currentPath / _lastSpanBase updates here
                    // Clean old files (keep last 3)
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(TempDir, "cronator_span_*.bmp")
                                                .OrderByDescending(f => f).Skip(3))
                            try { File.Delete(f); } catch { }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("SPAN update error: " + ex.Message);
            }
            finally
            {
                baseWall?.Dispose();
            }
        }


        // ===== Drawing helpers =====
        private static void DrawOverlay(Graphics g, Rectangle monitorRect, int monitorIndex, Color? overlayColor = null, Rectangle? virt = null)
        {
            // Translate to virtual origin if provided
            var v = virt ?? new Rectangle(0, 0, monitorRect.Width, monitorRect.Height);
            int ox = monitorRect.Left - v.Left;
            int oy = monitorRect.Top - v.Top;

            var overlay = overlayColor.HasValue ? Color.FromArgb(255, overlayColor.Value) : Color.FromArgb(255, 0, 122, 255);
            using var brush = new SolidBrush(overlay);
            var inset = new Rectangle(ox + (int)(monitorRect.Width * 0.05),
                                      oy + (int)(monitorRect.Height * 0.06),
                                      (int)(monitorRect.Width * 0.90),
                                      (int)(monitorRect.Height * 0.80));
            g.FillRectangle(brush, inset);

            using var border = new Pen(Color.White, Math.Max(6, monitorRect.Width / 200));
            g.DrawRectangle(border, inset);

            // text
            using var f1 = new Font("Segoe UI", Math.Max(18, monitorRect.Width / 40), FontStyle.Bold);
            using var f2 = new Font("Consolas", Math.Max(16, monitorRect.Width / 45), FontStyle.Bold);
            using var f3 = new Font("Segoe UI", Math.Max(14, monitorRect.Width / 60), FontStyle.Regular);
            using var tx = new SolidBrush(Color.Black);
            g.DrawString($"CRONATOR OVERLAY (monitor {monitorIndex})", f1, tx, inset.Left + 20, inset.Top + 20);
            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss"), f2, tx, inset.Left + 20, inset.Top + (int)(f1.Size * 2));
            g.DrawString("t <sec> cycles color | color <name|random> | q to restore & quit", f3, tx, inset.Left + 20, inset.Bottom - (int)(f3.Size * 2));
        }


        private static Image? LoadImageUnlocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
                return new Bitmap(img); // clone so the stream can close
            }
            catch { return null; }
        }

        private static bool SaveBmpWithRetry(Bitmap bmp, string path, int retries = 3, int delayMs = 40)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    bmp.Save(path, ImageFormat.Bmp);
                    return true;
                }
                catch
                {
                    Thread.Sleep(delayMs);
                }
            }
            return false;
        }

        private static void DrawFillImage(Graphics g, Image img, Rectangle monitorRect, Rectangle virt)
        {
            // "Fill" scaling per monitor
            float rw = (float)monitorRect.Width / img.Width;
            float rh = (float)monitorRect.Height / img.Height;
            float scale = Math.Max(rw, rh);
            int dw = (int)(img.Width * scale);
            int dh = (int)(img.Height * scale);
            int dx = (monitorRect.Left - virt.Left) + (monitorRect.Width - dw) / 2;
            int dy = (monitorRect.Top - virt.Top) + (monitorRect.Height - dh) / 2;
            g.DrawImage(img, new Rectangle(dx, dy, dw, dh), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
        }
        
        // ===== Tray hooks (called by tray/UI thread) =====
        internal static void TrayExitRequested()
        {
            StopTimer();
            TryRestore();
            Cronator.Tray.Stop();
            Environment.Exit(0);
        }

        internal static string[] TrayGetMonitorList()
        {
            // Names like "2560x1440 primary" etc.
            var screens = System.Windows.Forms.Screen.AllScreens.ToList();
            return screens.Select(s => $"{s.Bounds.Width}x{s.Bounds.Height}" + (s.Primary ? " (primary)" : ""))
                        .ToArray();
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
            }
        }

        internal static void TraySetColor(string name) => SetColor(name);

        internal static string TrayGetColorName()
        {
            // Simple exposure of current color name; if you want exact, keep a separate field.
            return "green"; // minimal; optional: track last chosen name in a field and return it
        }

        internal static void TraySetTimer(int? seconds)
        {
            if (seconds.HasValue && seconds.Value > 0) StartTimer(seconds.Value);
            else StopTimer();
        }

        internal static void TrayUpdateOnce()
        {
            if (_dw != null) { ListMonitorsCOM(out var ids, out var rects); UpdateSelectedCOM(ids, rects); }
            else { var screens = System.Windows.Forms.Screen.AllScreens.ToList(); UpdateSelectedSPAN(screens); }
        }

    }
}
