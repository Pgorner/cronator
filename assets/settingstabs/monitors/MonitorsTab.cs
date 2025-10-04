using System.Windows.Forms;

namespace Cronator.Tabs.Monitors
{
    // Duck-typed by loader: needs Id, Title, CreateControl()
    public sealed class MonitorsTab
    {
        public string Id => "monitors";
        public string Title => "Monitors";

        public Control CreateControl()
        {
            return new MonitorLayoutControl { Dock = DockStyle.Fill };
        }
    }
}
