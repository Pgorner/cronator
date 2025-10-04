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

        // ---------------------------- PUBLIC API ----------------------------

        /// <summary>
        /// Start tray icon thread. Pass an .ico path (static) and/or a .gif path (animated).
        /// If both are null, uses a generated green dot.
        /// </summary>
        internal static void Start(string? icoPath = null, string? gifPath = null, int gifFps = 8)
        {
            if (_started) return;
            _started = true;

            _ui = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    // install a WindowsForms sync context for this thread
                    _ctx = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(_ctx);

                    // Context menu
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

                    // Create icon
                    Icon baseIcon;
                    if (!string.IsNullOrWhiteSpace(icoPath) && File.Exists(icoPath))
                        baseIcon = new Icon(icoPath);
                    else
                        baseIcon = CreateColoredDotIcon(Color.LimeGreen); // tiny, generated

                    _icon = new NotifyIcon
                    {
                        Text = "Cronator",
                        Icon = baseIcon,
                        Visible = true,
                        ContextMenuStrip = menu
                    };

                    _icon.MouseClick += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Left) ShowSettings();
                    };

                    // Optional: auto-start GIF if provided
                    if (!string.IsNullOrWhiteSpace(gifPath) && File.Exists(gifPath))
                        StartGifAnimationFromFile(gifPath!, gifFps);

                    _icon.BalloonTipTitle = "Cronator";
                    _icon.BalloonTipText = "Running. Right-click for Settings or Exit.";
                    _icon.ShowBalloonTip(2000);

                    // Controllable message loop
                    _appCtx = new ApplicationContext();
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
        }

        /// <summary>
        /// Start using embedded resources. resource names are like "Cronator.Assets.cronator.ico".
        /// Pass null to skip.
        /// </summary>
        internal static void StartFromEmbedded(string? icoResource = null, string? gifResource = null, int gifFps = 8)
        {
            if (_started) return;
            _started = true;

            _ui = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    _ctx = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(_ctx);

                    // Context menu
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

                    // Base icon: embedded .ico or generated dot
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

                    _icon.MouseClick += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Left) ShowSettings();
                    };

                    // Optional: auto-start GIF from embedded resource
                    if (!string.IsNullOrWhiteSpace(gifResource))
                        StartGifAnimationFromResource(gifResource!, gifFps);

                    _icon.BalloonTipTitle = "Cronator";
                    _icon.BalloonTipText = "Running. Right-click for Settings or Exit.";
                    _icon.ShowBalloonTip(2000);

                    _appCtx = new ApplicationContext();
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
        }

        /// <summary>
        /// Cleanly stop the tray thread and message loop, then join the thread.
        /// </summary>
        internal static void Stop()
        {
            try
            {
                // Marshal all UI cleanup to the tray thread
                if (_ctx != null)
                {
                    _ctx.Post(_ =>
                    {
                        try
                        {
                            // close any settings forms opened from tray
                            foreach (Form f in Application.OpenForms)
                                if (f is SettingsForm) f.Close();
                        }
                        catch { }

                        try
                        {
                            // Stop animation on UI thread (timer lives there)
                            StopGifAnimation();
                        }
                        catch { }

                        try
                        {
                            // Ask the message loop to exit
                            _appCtx?.ExitThread();
                        }
                        catch { }
                    }, null);
                }
            }
            catch { }

            // Wait for the UI thread to terminate so the process can exit
            try
            {
                if (_ui != null && _ui.IsAlive)
                    _ui.Join(1500);
            }
            catch { }

            _started = false;
        }

        /// <summary>Swap to a static icon from file at runtime.</summary>
        internal static void SetStaticIconFromFile(string path)
        {
            if (_icon == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            StopGifAnimation();
            try
            {
                using var ico = new Icon(path);
                _icon.Icon = (Icon)ico.Clone();
            }
            catch (Exception ex) { Console.WriteLine("[Tray] SetStaticIconFromFile error: " + ex.Message); }
        }

        /// <summary>Swap to a static icon from embedded resource at runtime.</summary>
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

        /// <summary>Regenerate the small colored dot icon (e.g., to reflect selected color).</summary>
        internal static void SetColoredDotIcon(Color color)
        {
            if (_icon == null) return;
            StopGifAnimation();
            var ico = CreateColoredDotIcon(color);
            _icon.Icon = ico; // NotifyIcon owns it; we'll destroy on Stop()
        }

        // --------------------- GIF animation (file or resource) ---------------------

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
            StopGifAnimation(); // ensure clean state
            try
            {
                var fd = new FrameDimension(gif.FrameDimensionsList[0]);
                int count = gif.GetFrameCount(fd);
                if (count <= 0) return;

                int target = 16; // tray sizes are tiny; 16px looks crisp
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
                    IntPtr hIcon = bmp.GetHicon();         // unmanaged handle
                    lock (_hiconLock) _ownedHicons.Add(hIcon);
                    frames[i] = Icon.FromHandle(hIcon);    // wrap for NotifyIcon
                }

                _animFrames = frames;
                _animIndex = 0;

                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Interval = Math.Max(50, 1000 / Math.Max(1, fps)); // sensible minimum
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

        /// <summary>Stop animation and free frame icons. Must run on the tray thread.</summary>
        internal static void StopGifAnimation()
        {
            try
            {
                if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); _animTimer = null; }
                if (_animFrames != null)
                {
                    foreach (var ico in _animFrames)
                    {
                        try { ico.Dispose(); } catch { }
                    }
                    _animFrames = null;
                }
            }
            catch { }

            // Destroy unmanaged HICONs we created
            lock (_hiconLock)
            {
                foreach (var h in _ownedHicons)
                {
                    try { DestroyIcon(h); } catch { }
                }
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

                // Requires a parameterless SettingsForm() overload (you added one earlier).
                var form = new SettingsForm();
                form.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tray] ShowSettings error: " + ex.Message);
            }
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

            // StopGifAnimation must have run on UI thread before ExitThread;
            // here we only ensure unmanaged handles list is empty as a fallback.
            try { StopGifAnimation(); } catch { }
        }

        /// <summary>Create a tiny 16x16 colored dot icon.</summary>
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
            var hIcon = bmp.GetHicon(); // unmanaged; tracked in list
            lock (_hiconLock) _ownedHicons.Add(hIcon);
            return Icon.FromHandle(hIcon);
        }

        private static Stream? OpenResourceStream(string resourceName)
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            }
            catch { return null; }
        }
    }
}
