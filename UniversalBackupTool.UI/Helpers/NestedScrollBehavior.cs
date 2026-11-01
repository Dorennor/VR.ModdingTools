using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UniversalBackupTool.UI.Helpers
{
    /// <summary>
    /// For a ScrollViewer nested inside another one: when the inner one is already at its top/bottom edge (or cannot
    /// scroll), the mouse wheel is passed on to the outer ScrollViewer instead of being swallowed.
    /// </summary>
    public static class NestedScrollBehavior
    {
        public static readonly DependencyProperty PassWheelAtEdgesProperty =
            DependencyProperty.RegisterAttached(
                "PassWheelAtEdges",
                typeof(bool),
                typeof(NestedScrollBehavior),
                new PropertyMetadata(false, OnPassWheelAtEdgesChanged));

        public static bool GetPassWheelAtEdges(DependencyObject obj) => (bool)obj.GetValue(PassWheelAtEdgesProperty);

        public static void SetPassWheelAtEdges(DependencyObject obj, bool value) => obj.SetValue(PassWheelAtEdgesProperty, value);

        private static void OnPassWheelAtEdgesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer viewer)
                return;

            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;

            if ((bool)e.NewValue)
                viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer viewer || e.Handled)
                return;

            const double tolerance = 0.5;
            var atTop = viewer.VerticalOffset <= tolerance;
            var atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - tolerance;
            var scrollingUp = e.Delta > 0;

            if ((scrollingUp && !atTop) || (!scrollingUp && !atBottom))
                return;

            var outer = FindParentScrollViewer(viewer);

            if (outer is null)
                return;

            e.Handled = true;

            outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = viewer
            });
        }

        private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
        {
            var current = VisualTreeHelper.GetParent(child);

            while (current is not null)
            {
                if (current is ScrollViewer viewer)
                    return viewer;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}