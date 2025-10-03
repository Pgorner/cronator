using System;
using System.Linq;
using System.Windows.Forms;

namespace Cronator
{
    internal class SettingsForm : Form
    {
        private ComboBox cboMonitor = new();
        private ComboBox cboColor = new();
        private NumericUpDown numInterval = new();
        private Button btnApply = new();
        private Button btnClose = new();

        public SettingsForm()
        {
            Text = "Cronator Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 360;
            Height = 220;

            var lblMon = new Label { Text = "Monitor:", Left = 16, Top = 20, Width = 80 };
            cboMonitor.Left = 110; cboMonitor.Top = 16; cboMonitor.Width = 210;

            var lblColor = new Label { Text = "Color:", Left = 16, Top = 60, Width = 80 };
            cboColor.Left = 110; cboColor.Top = 56; cboColor.Width = 210;
            cboColor.DropDownStyle = ComboBoxStyle.DropDownList;
            cboColor.Items.AddRange(new object[] { "red", "orange", "yellow", "green", "blue", "purple", "lightblue", "random" });

            var lblInt = new Label { Text = "Timer (s):", Left = 16, Top = 100, Width = 80 };
            numInterval.Left = 110; numInterval.Top = 96; numInterval.Width = 80;
            numInterval.Minimum = 0; // 0 = off
            numInterval.Maximum = 3600;
            numInterval.Value = 0;

            btnApply.Text = "Apply";
            btnApply.Left = 170; btnApply.Top = 140; btnApply.Width = 70;
            btnApply.Click += (_, __) => Apply();

            btnClose.Text = "Close";
            btnClose.Left = 250; btnClose.Top = 140; btnClose.Width = 70;
            btnClose.Click += (_, __) => Close();

            Controls.AddRange(new Control[] { lblMon, cboMonitor, lblColor, cboColor, lblInt, numInterval, btnApply, btnClose });

            // populate monitors
            var mons = Program.TrayGetMonitorList();
            foreach (var (index, label) in mons.Select((l, i) => (i, l)))
                cboMonitor.Items.Add($"{index}: {label}");
            if (cboMonitor.Items.Count > 0) cboMonitor.SelectedIndex = Program.TrayGetSelectedMonitorIndex();

            // current color
            var curColor = Program.TrayGetColorName();
            var idx = cboColor.Items.IndexOf(curColor);
            cboColor.SelectedIndex = idx >= 0 ? idx : 3; // green default
        }

        private void Apply()
        {
            if (cboMonitor.SelectedIndex >= 0)
                Program.TraySelectMonitor(cboMonitor.SelectedIndex);

            if (cboColor.SelectedItem is string name)
                Program.TraySetColor(name);

            int sec = (int)numInterval.Value;
            Program.TraySetTimer(sec > 0 ? sec : (int?)null);

            // force a visible update
            Program.TrayUpdateOnce();

            MessageBox.Show(this, "Applied.", "Cronator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
