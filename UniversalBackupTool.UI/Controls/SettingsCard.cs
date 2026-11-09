using System.Windows;
using System.Windows.Controls;

namespace UniversalBackupTool.UI.Controls
{
    /// <summary>
    /// A Windows 11 Settings style row: icon, header and description on the left, the content (the control) on the right.
    /// With <see cref="IsStacked"/> the content is placed below the text and stretches, for wide inputs such as paths.
    /// A card too narrow to keep its text readable next to the content moves the content below the text by itself
    /// (<see cref="IsAutoStacked"/>). The template lives in Styles/AppStyles.xaml.
    /// </summary>
    public class SettingsCard : ContentControl
    {
        /// <summary>Padding, icon column and the gap before the content.</summary>
        private const double CardChromeWidth = 90;

        private const double MinTextWidth = 260;

        private FrameworkElement? _contentHost;

        private static readonly DependencyPropertyKey IsAutoStackedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsAutoStacked), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false));

        public static readonly DependencyProperty IsAutoStackedProperty = IsAutoStackedPropertyKey.DependencyProperty;

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(object), typeof(SettingsCard), new PropertyMetadata(null));

        public static readonly DependencyProperty IsStackedProperty =
            DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false));

        public string? Header
        {
            get => (string?)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public string? Description
        {
            get => (string?)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public object? Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public bool IsStacked
        {
            get => (bool)GetValue(IsStackedProperty);
            set => SetValue(IsStackedProperty, value);
        }

        public bool IsAutoStacked => (bool)GetValue(IsAutoStackedProperty);

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _contentHost = GetTemplateChild("ContentHost") as FrameworkElement;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);

            if (sizeInfo.WidthChanged)
                UpdateAutoStacked(sizeInfo.NewSize.Width);
        }

        /// <remarks>
        /// The content keeps its natural width in both layouts (left aligned when auto-stacked), so moving it cannot change
        /// this result and the layout does not flip back and forth.
        /// </remarks>
        private void UpdateAutoStacked(double cardWidth)
        {
            if (_contentHost is null || IsStacked)
                return;

            var textWidth = cardWidth - CardChromeWidth - _contentHost.DesiredSize.Width;

            SetValue(IsAutoStackedPropertyKey, textWidth < MinTextWidth);
        }
    }
}