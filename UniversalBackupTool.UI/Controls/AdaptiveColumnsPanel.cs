using System.Windows;
using System.Windows.Controls;

namespace UniversalBackupTool.UI.Controls
{
    /// <summary>
    /// Lays its children out in as many equal columns as fit (at least <see cref="MinColumnWidth"/> wide, at most
    /// <see cref="MaxColumns"/>), filling row by row. One column on a small window, more when maximized. Children of one
    /// row get the same height, so neighbouring cards line up. Vertical gaps come from the children's own margins.
    /// </summary>
    public class AdaptiveColumnsPanel : Panel
    {
        public static readonly DependencyProperty MinColumnWidthProperty =
            DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(AdaptiveColumnsPanel),
                new FrameworkPropertyMetadata(540.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty MaxColumnsProperty =
            DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveColumnsPanel),
                new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnSpacingProperty =
            DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(AdaptiveColumnsPanel),
                new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinColumnWidth
        {
            get => (double)GetValue(MinColumnWidthProperty);
            set => SetValue(MinColumnWidthProperty, value);
        }

        public int MaxColumns
        {
            get => (int)GetValue(MaxColumnsProperty);
            set => SetValue(MaxColumnsProperty, value);
        }

        public double ColumnSpacing
        {
            get => (double)GetValue(ColumnSpacingProperty);
            set => SetValue(ColumnSpacingProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = GetColumnCount(availableSize.Width);
            var rowHeight = 0.0;
            var totalHeight = 0.0;
            var maxChildWidth = 0.0;
            var column = 0;

            foreach (var child in GetVisibleChildren())
            {
                var width = double.IsInfinity(availableSize.Width)
                    ? double.PositiveInfinity
                    : GetColumnBounds(column, columns, availableSize.Width).Width;

                child.Measure(new Size(width, double.PositiveInfinity));

                maxChildWidth = Math.Max(maxChildWidth, child.DesiredSize.Width);
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

                if (++column == columns)
                {
                    totalHeight += rowHeight;
                    rowHeight = 0;
                    column = 0;
                }
            }

            totalHeight += rowHeight;

            return new Size(double.IsInfinity(availableSize.Width) ? maxChildWidth : availableSize.Width, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = GetColumnCount(finalSize.Width);
            var children = GetVisibleChildren();
            var top = 0.0;

            for (var rowStart = 0; rowStart < children.Count; rowStart += columns)
            {
                var row = children.Skip(rowStart).Take(columns).ToList();
                var rowHeight = row.Max(child => child.DesiredSize.Height);

                for (var column = 0; column < row.Count; column++)
                {
                    var bounds = GetColumnBounds(column, columns, finalSize.Width);
                    row[column].Arrange(new Rect(bounds.Left, top, bounds.Width, rowHeight));
                }

                top += rowHeight;
            }

            return finalSize;
        }

        private List<UIElement> GetVisibleChildren() =>
            InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToList();

        private int GetColumnCount(double width)
        {
            if (double.IsInfinity(width))
                return 1;

            var fitting = (int)Math.Floor((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing));

            return Math.Clamp(fitting, 1, Math.Max(1, MaxColumns));
        }

        /// <summary>
        /// Column edges on whole pixels so card borders stay sharp; the last column ends exactly at the panel's right edge,
        /// in line with the full-width cards above and below.
        /// </summary>
        private (double Left, double Width) GetColumnBounds(int column, int columns, double totalWidth)
        {
            var step = (totalWidth + ColumnSpacing) / columns;
            var left = Math.Round(column * step);
            var right = column == columns - 1 ? totalWidth : Math.Round((column + 1) * step) - ColumnSpacing;

            return (left, Math.Max(0, right - left));
        }
    }
}