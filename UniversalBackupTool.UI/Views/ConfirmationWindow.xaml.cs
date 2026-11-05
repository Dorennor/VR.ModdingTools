using System.Windows;

using Wpf.Ui.Controls;

namespace UniversalBackupTool.UI.Views
{
    /// <summary>
    /// Modal Yes/No question in the application's own style, used instead of the system message box.
    /// </summary>
    public partial class ConfirmationWindow : FluentWindow
    {
        private const double MaxScreenHeightShare = 0.8;

        public ConfirmationWindow(string title, string message)
        {
            InitializeComponent();

            Title = title;
            HeadingText.Text = title;
            MessageText.Text = message;
            MaxHeight = SystemParameters.WorkArea.Height * MaxScreenHeightShare;

            Loaded += (_, _) => NoButton.Focus();
        }

        private void OnYesClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}