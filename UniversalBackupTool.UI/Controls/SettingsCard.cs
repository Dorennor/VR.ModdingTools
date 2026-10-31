using System.Windows;
using System.Windows.Controls;

namespace UniversalBackupTool.UI.Controls
{
    /// <summary>
    /// A Windows 11 Settings style row: icon, header and description on the left, the content (the control) on the right.
    /// With <see cref="IsStacked"/> the content is placed below the text and stretches, for wide inputs such as paths.
    /// The template lives in Styles/AppStyles.xaml.
    /// </summary>
    public class SettingsCard : ContentControl
    {
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
    }
}
