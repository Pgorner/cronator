using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Cronator
{
    internal static class Tray
    {
        private static NotifyIcon? _icon;
        private static Thread? _ui;
        private static volatile bool _started;

        // Message loop control (for clean shutdown)
        private static ApplicationContext? _appCtx;
        private static SynchronizationContext? _ctx;

        // Animation state (must be used on tray/UI thread)
        private static System.Windows.Forms.Timer? _animTimer;
        private static Icon[]? _animFrames;
        private static int _animIndex;

        // Track HICONs we create so we can destroy them
        private static readonly object _hiconLock = new();
        private static readonly System.Collections.Generic.List<IntPtr> _ownedHicons = new();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static bool IsTrayThread => _ui != null && Thread.CurrentThread == _ui;

        // ---------------------------- PUBLIC API ----------------------------

        internal static void Start(string? icoPath = null, string? gifPath = null, int gifFps = 8)
        {
            if (_started) return;
            _started = true;

            var ready = new ManualResetEventSlim(false);

            _ui = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    _ctx = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(_ctx);

                    var menu = new ContextMenuStrip();
                    var settingsItem = new ToolStripMenuItem("Settings", null, (_, __) => ShowSettings());
                    var toggleAnim   = new ToolStripMenuItem("Toggle Animation", null, (_, __) =>
                    {
                        if (_animTimer == null)
                        {
                            if (!string.IsNullOrWhiteSpace(gifPath))
                                StartGifAnimationFromFile(gifPath!, gifFps);
                        }
                        else StopGifAnimation();
                    });
                    var exitItem     = new ToolStripMenuItem("Exit", null, (_, __) => Program.TrayExitRequested());

                    menu.Items.Add(settingsItem);
                    menu.Items.Add(toggleAnim);
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(exitItem);

                    Icon baseIcon;
                    if (!string.IsNullOrWhiteSpace(icoPath) && File.Exists(icoPath))
                        baseIcon = new Icon(icoPath);
                    else
                        baseIcon = CreateColoredDotIcon(Color.LimeGreen);

                    _icon = new NotifyIcon
                    {
                        Text = "Cronator",
                        Icon = baseIcon,
                        Visible = true,
                        ContextMenuStrip = menu
                    };

                    _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

                    if (!string.IsNullOrWhiteSpace(gifPath) && File.Exists(gifPath))
                        StartGifAnimationFromFile(gifPath!, gifFps);

                    _icon.BalloonTipTitle = "Cronator";
                    _icon.BalloonTipText = "Running. Right-click for Settings or Exit.";
                    _icon.ShowBalloonTip(2000);

                    _appCtx = new ApplicationContext();
                    ready.Set();
                    Application.Run(_appCtx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Tray] ERROR: " + ex);
                }
                finally
                {
                    CleanupIcon();
                    _appCtx = null;
                    _ctx = null;
                }
            });

            _ui.SetApartmentState(ApartmentState.STA);
            _ui.IsBackground = true;
            _ui.Start();
            ready.Wait();
        }

        internal static void StartFromEmbedded(string? icoResource = null, string? gifResource = null, int gifFps = 8)
        {
            if (_started) return;
            _started = true;

            var ready = new ManualResetEventSlim(false);

            _ui = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    _ctx = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(_ctx);

                    var menu = new ContextMenuStrip();
                    var settingsItem = new ToolStripMenuItem("Settings", null, (_, __) => ShowSettings());
                    var toggleAnim   = new ToolStripMenuItem("Toggle Animation", null, (_, __) =>
                    {
                        if (_animTimer == null)
                        {
                            if (!string.IsNullOrWhiteSpace(gifResource))
                                StartGifAnimationFromResource(gifResource!, gifFps);
                        }
                        else StopGifAnimation();
                    });
                    var exitItem     = new ToolStripMenuItem("Exit", null, (_, __) => Program.TrayExitRequested());

                    menu.Items.Add(settingsItem);
                    menu.Items.Add(toggleAnim);
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(exitItem);

                    Icon baseIcon;
                    if (!string.IsNullOrWhiteSpace(icoResource))
                    {
                        using var s = OpenResourceStream(icoResource!);
                        baseIcon = (s != null) ? new Icon(s) : CreateColoredDotIcon(Color.LimeGreen);
                    }
                    else
                    {
                        baseIcon = CreateColoredDotIcon(Color.LimeGreen);
                    }

                    _icon = new NotifyIcon
                    {
                        Text = "Cronator",
                        Icon = baseIcon,
                        Visible = true,
                        ContextMenuStrip = menu
                    };

                    _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

                    if (!string.IsNullOrWhiteSpace(gifResource))
                        StartGifAnimationFromResource(gifResource!, gifFps);

                    _icon.BalloonTipTitle = "Cronator";
                    _icon.BalloonTipText = "Running. Right-click for Settings or Exit.";
                    _icon.ShowBalloonTip(2000);

                    _appCtx = new ApplicationContext();
                    ready.Set();
                    Application.Run(_appCtx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Tray] ERROR: " + ex);
                }
                finally
                {
                    CleanupIcon();
                    _appCtx = null;
                    _ctx = null;
                }
            });

            _ui.SetApartmentState(ApartmentState.STA);
            _ui.IsBackground = true;
            _ui.Start();
            ready.Wait();
        }

        /// <summary>Cleanly stop the tray thread and message loop, then join the thread.</summary>
        internal static void Stop()
        {
            // If we’re *on* the tray thread, do inline shutdown (no Join on ourselves)
            if (IsTrayThread)
            {
                try
                {
                    try
                    {
                        foreach (Form f in Application.OpenForms)
                            if (f is SettingsForm) f.Close();
                    }
                    catch { }

                    try { StopGifAnimation(); } catch { }

                    try
                    {
                        if (_icon != null)
                        {
                            _icon.Visible = false;
                            _icon.Dispose();
                            _icon = null;
                        }
                    }
                    catch { }

                    try { _appCtx?.ExitThread(); } catch { }
                }
                finally
                {
                    // final safety for unmanaged HICONs
                    try
                    {
                        lock (_hiconLock)
                        {
                            foreach (var h in _ownedHicons) { try { DestroyIcon(h); } catch { } }
                            _ownedHicons.Clear();
                        }
                    }
                    catch { }

                    _appCtx = null;
                    _ctx = null;
                    _started = false;
                    // Do NOT touch _ui/join here; we *are* that thread and ExitThread() will unwind it.
                }
                return;
            }

            // Normal path: marshal to UI thread and then Join it
            try
            {
                if (_ctx != null)
                {
                    _ctx.Post(_ =>
                    {
                        try
                        {
                            foreach (Form f in Application.OpenForms)
                                if (f is SettingsForm) f.Close();
                        }
                        catch { }

                        try { StopGifAnimation(); } catch { }

                        try
                        {
                            if (_icon != null)
                            {
                                _icon.Visible = false;
                                _icon.Dispose();
                                _icon = null;
                            }
                        }
                        catch { }

                        try { _appCtx?.ExitThread(); } catch { }
                    }, null);
                }
                else
                {
                    try { StopGifAnimation(); } catch { }
                    try
                    {
                        if (_icon != null)
                        {
                            _icon.Visible = false;
                            _icon.Dispose();
                            _icon = null;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                if (_ui != null && _ui.IsAlive)
                {
                    if (!_ui.Join(3000))
                        Console.WriteLine("[Tray] UI thread did not exit in time.");
                }
            }
            catch { }

            try
            {
                lock (_hiconLock)
                {
                    foreach (var h in _ownedHicons) { try { DestroyIcon(h); } catch { } }
                    _ownedHicons.Clear();
                }
            }
            catch { }

            _appCtx = null;
            _ctx = null;
            _ui = null;
            _started = false;
        }

        internal static void SetStaticIconFromFile(string path)
        {
            if (_icon == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            StopGifAnimation();
            try { using var ico = new Icon(path); _icon.Icon = (Icon)ico.Clone(); }
            catch (Exception ex) { Console.WriteLine("[Tray] SetStaticIconFromFile error: " + ex.Message); }
        }

        internal static void SetStaticIconFromResource(string resourceName)
        {
            if (_icon == null || string.IsNullOrWhiteSpace(resourceName)) return;
            StopGifAnimation();
            try
            {
                using var s = OpenResourceStream(resourceName);
                if (s == null) return;
                using var ico = new Icon(s);
                _icon.Icon = (Icon)ico.Clone();
            }
            catch (Exception ex) { Console.WriteLine("[Tray] SetStaticIconFromResource error: " + ex.Message); }
        }

        internal static void SetColoredDotIcon(Color color)
        {
            if (_icon == null) return;
            StopGifAnimation();
            var ico = CreateColoredDotIcon(color);
            _icon.Icon = ico;
        }

        // --------------------- GIF animation ---------------------

        internal static void StartGifAnimationFromFile(string gifPath, int fps = 8)
        {
            if (_icon == null || !File.Exists(gifPath)) return;
            using var img = Image.FromFile(gifPath);
            StartGifAnimationFromImage(img, fps);
        }

        internal static void StartGifAnimationFromResource(string resourceName, int fps = 8)
        {
            if (_icon == null) return;
            using var s = OpenResourceStream(resourceName);
            if (s == null) return;
            using var img = Image.FromStream(s, useEmbeddedColorManagement: false, validateImageData: false);
            StartGifAnimationFromImage(img, fps);
        }

        private static void StartGifAnimationFromImage(Image gif, int fps)
        {
            StopGifAnimation();
            try
            {
                var fd = new FrameDimension(gif.FrameDimensionsList[0]);
                int count = gif.GetFrameCount(fd);
                if (count <= 0) return;

                int target = 16;
                var frames = new Icon[count];

                for (int i = 0; i < count; i++)
                {
                    gif.SelectActiveFrame(fd, i);
                    using var bmp = new Bitmap(target, target);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(gif, new Rectangle(0, 0, target, target));
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    lock (_hiconLock) _ownedHicons.Add(hIcon);
                    frames[i] = Icon.FromHandle(hIcon);
                }

                _animFrames = frames;
                _animIndex = 0;

                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Interval = Math.Max(50, 1000 / Math.Max(1, fps));
                _animTimer.Tick += (_, __) =>
                {
                    if (_icon == null || _animFrames == null || _animFrames.Length == 0) return;
                    _icon.Icon = _animFrames[_animIndex];
                    _animIndex = (_animIndex + 1) % _animFrames.Length;
                };
                _animTimer.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tray] GIF animation error: " + ex.Message);
                StopGifAnimation();
            }
        }

        internal static void StopGifAnimation()
        {
            try
            {
                if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); _animTimer = null; }
                if (_animFrames != null)
                {
                    foreach (var ico in _animFrames) { try { ico.Dispose(); } catch { } }
                    _animFrames = null;
                }
            }
            catch { }

            lock (_hiconLock)
            {
                foreach (var h in _ownedHicons) { try { DestroyIcon(h); } catch { } }
                _ownedHicons.Clear();
            }
        }

        // ---------------------------- INTERNALS ----------------------------

        private static void ShowSettings()
        {
            try
            {
                foreach (Form f in Application.OpenForms)
                    if (f is SettingsForm) { f.Activate(); return; }

                var form = new SettingsForm();
                form.Show();
            }
            catch (Exception ex) { Console.WriteLine("[Tray] ShowSettings error: " + ex.Message); }
        }

        private static void CleanupIcon()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
            }
            catch { }

            try { StopGifAnimation(); } catch { }
        }

        private static Icon CreateColoredDotIcon(Color color)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using var b = new SolidBrush(color);
                g.FillEllipse(b, 2, 2, 12, 12);
                g.DrawEllipse(Pens.Black, 2, 2, 12, 12);
            }
            var hIcon = bmp.GetHicon();
            lock (_hiconLock) _ownedHicons.Add(hIcon);
            return Icon.FromHandle(hIcon);
        }

        private static Stream? OpenResourceStream(string resourceName)
        {
            try { return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName); }
            catch { return null; }
        }
    }
}
