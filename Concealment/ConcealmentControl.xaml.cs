#region

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Torch.Views;

#endregion

namespace Concealment
{
    /// <summary>
    ///     Interaction logic for ConcealmentControl.xaml
    /// </summary>
    public partial class ConcealmentControl : UserControl
    {
        public ConcealmentControl()
        {
            InitializeComponent();
        }

        public ConcealmentPlugin Plugin => (ConcealmentPlugin)DataContext;

        private void RevealSelected_OnClick(object sender, RoutedEventArgs e)
        {
            var groups = Concealed.SelectedItems.Cast<ConcealGroup>().ToList();
            Concealed.SelectedItems.Clear();
            if (!groups.Any())
                return;

            var p = Plugin;
            Plugin.Torch.InvokeBlocking(delegate
            {
                foreach (var current in groups)
                    p.RevealGroup(current);
            });
        }

        private void Reveal_OnClick(object sender, RoutedEventArgs e)
        {
            var p = Plugin;
            Plugin.Torch.Invoke(delegate { p.RevealNearbyGrids(p.Settings.Data.RevealDistance); });
        }

        private void Conceal_OnClick(object sender, RoutedEventArgs e)
        {
            var p = Plugin;
            Plugin.Torch.Invoke(delegate { p.ConcealDistantGrids(p.Settings.Data.ConcealDistance); });
        }

        private void EditExclusion_OnClick(object sender, RoutedEventArgs e)
        {
            var editor = new CollectionEditor() {Owner = Window.GetWindow(this)};
            editor.Edit(Plugin.Settings.Data.ExcludedSubtypes, "Excluded Subtypes");
        }
    }
}