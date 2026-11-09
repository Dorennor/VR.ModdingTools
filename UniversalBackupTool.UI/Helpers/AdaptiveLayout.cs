using System.Windows;

namespace UniversalBackupTool.UI.Helpers
{
    /// <summary>
    /// Publishes the size of a page to everything inside it as inherited attached properties, so styles and templates can
    /// react to the space the page really has (window size minus navigation pane and Output panel).
    /// AppPageStyle turns the tracking on for every page.
    /// </summary>
    public static class AdaptiveLayout
    {
        private const double NarrowPageWidth = 880;
        private const double ListHeightShare = 0.55;
        private const double MinListHeight = 200;

        public static readonly DependencyProperty TrackPageSizeProperty =
            DependencyProperty.RegisterAttached("TrackPageSize", typeof(bool), typeof(AdaptiveLayout),
                new PropertyMetadata(false, OnTrackPageSizeChanged));

        /// <summary>True when the page is too narrow for its usual margins and for commands next to the title.</summary>
        public static readonly DependencyProperty IsNarrowProperty =
            DependencyProperty.RegisterAttached("IsNarrow", typeof(bool), typeof(AdaptiveLayout),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Maximum height of a list with its own scroll area inside a page: a share of the page height.</summary>
        public static readonly DependencyProperty ListMaxHeightProperty =
            DependencyProperty.RegisterAttached("ListMaxHeight", typeof(double), typeof(AdaptiveLayout),
                new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetTrackPageSize(DependencyObject element) => (bool)element.GetValue(TrackPageSizeProperty);

        public static void SetTrackPageSize(DependencyObject element, bool value) => element.SetValue(TrackPageSizeProperty, value);

        public static bool GetIsNarrow(DependencyObject element) => (bool)element.GetValue(IsNarrowProperty);

        public static void SetIsNarrow(DependencyObject element, bool value) => element.SetValue(IsNarrowProperty, value);

        public static double GetListMaxHeight(DependencyObject element) => (double)element.GetValue(ListMaxHeightProperty);

        public static void SetListMaxHeight(DependencyObject element, double value) => element.SetValue(ListMaxHeightProperty, value);

        private static void OnTrackPageSizeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not FrameworkElement page)
                return;

            page.SizeChanged -= OnPageSizeChanged;

            if ((bool)e.NewValue)
                page.SizeChanged += OnPageSizeChanged;
        }

        private static void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var page = (FrameworkElement)sender;

            SetIsNarrow(page, e.NewSize.Width < NarrowPageWidth);
            SetListMaxHeight(page, Math.Max(MinListHeight, e.NewSize.Height * ListHeightShare));
        }
    }
}
