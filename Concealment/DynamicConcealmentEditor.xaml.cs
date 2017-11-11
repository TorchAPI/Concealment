using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Concealment
{
    /// <summary>
    /// Interaction logic for DynamicConcealmentEditor.xaml
    /// </summary>
    public partial class DynamicConcealmentEditor : Window
    {
        public DynamicConcealmentEditor()
        {
            InitializeComponent();
        }

        private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
        {
            var plugin = DataContext as ConcealmentPlugin;
            if (plugin == null) return;
            // ReSharper disable PossibleUnintendedReferenceComparison
            if (sender == OkayButton)
            {
                Close();
            } else if (sender == AddButton)
            {
                plugin.Settings.Data.DynamicConcealment.Add(new Settings.DynamicConcealSettings());
            } else if (sender == RemoveButton)
            {
                var entry = List.SelectedItem as Settings.DynamicConcealSettings;
                if (entry != null)
                    plugin.Settings.Data.DynamicConcealment.Remove(entry);
            }
            // ReSharper restore PossibleUnintendedReferenceComparison
        }
    }
}
