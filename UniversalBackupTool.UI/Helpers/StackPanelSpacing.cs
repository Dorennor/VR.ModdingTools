using System.Windows;
using System.Windows.Controls;

namespace UniversalBackupTool.UI.Helpers
{
    /// <summary>
    /// Adds a Spacing attached property to StackPanel (not available in WPF out of the box).
    /// </summary>
    public static class StackPanelSpacing
    {
        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.RegisterAttached(
                "Spacing",
                typeof(double),
                typeof(StackPanelSpacing),
                new PropertyMetadata(0d, OnSpacingChanged));

        public static double GetSpacing(DependencyObject obj) => (double)obj.GetValue(SpacingProperty);

        public static void SetSpacing(DependencyObject obj, double value) => obj.SetValue(SpacingProperty, value);

        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not StackPanel panel)
                return;

            panel.Loaded -= OnPanelLoaded;
            panel.Loaded += OnPanelLoaded;
        }

        private static void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            var panel = (StackPanel)sender;
            panel.Loaded -= OnPanelLoaded;

            var spacing = GetSpacing(panel);
            var isVertical = panel.Orientation == Orientation.Vertical;

            for (var i = 0; i < panel.Children.Count - 1; i++)
            {
                if (panel.Children[i] is not FrameworkElement child)
                    continue;

                var margin = child.Margin;

                child.Margin = isVertical
                    ? new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + spacing)
                    : new Thickness(margin.Left, margin.Top, margin.Right + spacing, margin.Bottom);
            }
        }
    }
}