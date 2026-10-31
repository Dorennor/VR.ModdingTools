using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UniversalBackupTool.UI.Helpers
{
    /// <summary>
    /// Output-window behaviour for a ListBox: follows new lines while scrolled to the bottom (like the Visual Studio
    /// Output window) and copies the selected lines with Ctrl+C.
    /// </summary>
    public static class OutputListBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(OutputListBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached("State", typeof(State), typeof(OutputListBehavior));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        /// <summary>
        /// Copies the given lines to the clipboard, one per line.
        /// </summary>
        public static void CopyToClipboard(IEnumerable<object> items)
        {
            var text = string.Join(Environment.NewLine, items.Select(item => item?.ToString()));

            if (text.Length > 0)
                Clipboard.SetText(text);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListBox listBox || !(bool)e.NewValue || listBox.GetValue(StateProperty) is not null)
                return;

            var state = new State(listBox);
            listBox.SetValue(StateProperty, state);

            listBox.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Copy,
                (_, _) => CopyToClipboard(listBox.SelectedItems.Cast<object>().OrderBy(listBox.Items.IndexOf)),
                (_, args) => args.CanExecute = listBox.SelectedItems.Count > 0));
        }

        private sealed class State
        {
            private readonly ListBox _listBox;
            private ScrollViewer? _scrollViewer;
            private INotifyCollectionChanged? _collection;

            public State(ListBox listBox)
            {
                _listBox = listBox;
                _listBox.Loaded += (_, _) => Attach();
                Attach();
            }

            private void Attach()
            {
                _scrollViewer ??= FindScrollViewer(_listBox);

                if (_collection is not null)
                    return;

                _collection = _listBox.Items;
                _collection.CollectionChanged += OnCollectionChanged;
            }

            private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action != NotifyCollectionChangedAction.Add)
                    return;

                _scrollViewer ??= FindScrollViewer(_listBox);

                // Only follow the output while the user has not scrolled up to read older lines.
                var atBottom = _scrollViewer is null || _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1;

                if (atBottom)
                    _listBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scrollViewer?.ScrollToEnd());
            }

            private static ScrollViewer? FindScrollViewer(DependencyObject root)
            {
                if (root is ScrollViewer viewer)
                    return viewer;

                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                {
                    var found = FindScrollViewer(VisualTreeHelper.GetChild(root, index));

                    if (found is not null)
                        return found;
                }

                return null;
            }
        }
    }
}
