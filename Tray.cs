using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Cronator
{
    internal static class Tray
    {
        private static NotifyIcon? _icon;
        private static Thread? _ui;
        private static volatile bool _started;

        internal static void Start()
        {
            if (_started) return;
            _started = true;

            _ui = new Thread(() =>
            {
                try
                {
                    // Classic init (works everywhere)
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    // Context menu
                    var menu = new ContextMenuStrip();
                    menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, __) => ShowSettings()));
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, __) => Program.TrayExitRequested()));

                    // Use a well-known system icon first (easy to spot).
                    // You can swap to CreateIcon() once you see it appear.
                    var icon = SystemIcons.Application; // <-- Change to CreateIcon() later if you want

                    _icon = new NotifyIcon
                    {
                        Text = "Cronator",
                        Icon = icon,
                        Visible = true,
                        ContextMenuStrip = menu
                    };

                    _icon.MouseClick += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Left) ShowSettings();
                    };

                    // Make it obvious it launched
                    try
                    {
                        _icon.BalloonTipTitle = "Cronator";
                        _icon.BalloonTipText = "Running. Right-click for Settings or Exit.";
                        _icon.ShowBalloonTip(3000);
                    }
                    catch { }

                    Console.WriteLine("[Tray] Icon created and visible.");
                    Application.Run(); // tray message loop
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Tray] ERROR: " + ex);
                }
                finally
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
                    Console.WriteLine("[Tray] Exited.");
                }
            });

            _ui.SetApartmentState(ApartmentState.STA);
            _ui.IsBackground = true;
            _ui.Start();
            Console.WriteLine("[Tray] Start requested.");
        }

        internal static void Stop()
        {
            try
            {
                if (Application.MessageLoop)
                    Application.ExitThread(); // ends Application.Run()
            }
            catch { }

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

            _started = false;
        }

        private static void ShowSettings()
        {
            try
            {
                foreach (Form f in Application.OpenForms)
                    if (f is SettingsForm) { f.Activate(); return; }

                var form = new SettingsForm();
                form.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tray] ShowSettings error: " + ex.Message);
            }
        }

        // If you want your custom green-dot icon later:
        private static Icon CreateIcon()
        {
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using var b = new SolidBrush(Color.LimeGreen);
                g.FillEllipse(b, 2, 2, 12, 12);
                g.DrawEllipse(Pens.Black, 2, 2, 12, 12);
            }
            // NOTE: We intentionally don't DestroyIcon here; the NotifyIcon owns it until Stop().
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
