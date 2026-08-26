using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RASTA.App.Controls
{
    /// <summary>
    /// A minimal zoom-and-pan wrapper: mouse wheel zooms about the cursor, left-button
    /// drag pans, double-click resets. Intended for wrapping a fixed-size, non-scrolling
    /// visual (e.g. the Sky Mosaic heatmap or Zenith Dome canvas in MosaicView.xaml) so it
    /// can be zoomed into and panned around without the visual itself needing to know
    /// anything about it.
    ///
    /// Adapted from the fuller ZoomAndPanControl in the ProcedureBuilder repo
    /// (Libraries/Procedure Builder Presentation/Controls), stripped of the parts that
    /// control doesn't need here: IScrollInfo/ScrollViewer integration (nothing wraps this
    /// in a ScrollViewer - panning is drag-based, not scrollbar-based), animated zoom, and
    /// content-focus tracking. It also composes its transform the other way round: Scale
    /// then Translate (not Translate then Scale), so ContentOffsetX/Y are plain screen-space
    /// pixels - which is what makes mouse-drag panning a direct 1:1 delta add with no
    /// scale division, and is really the only thing "simplified" changes about the maths.
    /// </summary>
    public class ZoomAndPanControl : ContentControl
    {
        /// <summary>Resets scale to 1 and offset to (0,0). Also invoked by a left-button double-click.</summary>
        public static readonly RoutedCommand ResetViewCommand = new(nameof(ResetViewCommand), typeof(ZoomAndPanControl));

        /// <summary>Scales/centers the content so it fits entirely within the current viewport.</summary>
        public static readonly RoutedCommand ZoomToFitCommand = new(nameof(ZoomToFitCommand), typeof(ZoomAndPanControl));

        public static readonly DependencyProperty ContentScaleProperty =
            DependencyProperty.Register(nameof(ContentScale), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(1.0, OnContentScaleChanged, CoerceContentScale));

        public static readonly DependencyProperty MinContentScaleProperty =
            DependencyProperty.Register(nameof(MinContentScale), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(0.25));

        public static readonly DependencyProperty MaxContentScaleProperty =
            DependencyProperty.Register(nameof(MaxContentScale), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(8.0));

        public static readonly DependencyProperty ContentOffsetXProperty =
            DependencyProperty.Register(nameof(ContentOffsetX), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(0.0, OnContentOffsetXChanged));

        public static readonly DependencyProperty ContentOffsetYProperty =
            DependencyProperty.Register(nameof(ContentOffsetY), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(0.0, OnContentOffsetYChanged));

        /// <summary>Multiplier applied per mouse-wheel notch (i.e. a wheel step zooms by this factor).</summary>
        public static readonly DependencyProperty ZoomStepProperty =
            DependencyProperty.Register(nameof(ZoomStep), typeof(double), typeof(ZoomAndPanControl),
                new FrameworkPropertyMetadata(1.2));

        private readonly ScaleTransform _scaleTransform = new();
        private readonly TranslateTransform _translateTransform = new();
        private FrameworkElement? _content;
        private Point? _lastDragPoint;

        /// <summary>
        /// True while the view should keep tracking "fit to viewport" automatically - i.e.
        /// since the last ZoomToFit() (including the initial state) nobody has zoomed or
        /// panned by hand. Set false the moment the user does either, so resizing the window
        /// afterwards doesn't fight a zoom/pan they deliberately chose; ZoomToFit() (and so
        /// ZoomToFitCommand) re-arms it, since asking to fit again is asking to resume
        /// tracking.
        /// </summary>
        private bool _autoFit = true;

        static ZoomAndPanControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ZoomAndPanControl), new FrameworkPropertyMetadata(typeof(ZoomAndPanControl)));
        }

        public ZoomAndPanControl()
        {
            CommandBindings.Add(new CommandBinding(ResetViewCommand, (_, _) => ResetView()));
            CommandBindings.Add(new CommandBinding(ZoomToFitCommand, (_, _) => ZoomToFit()));

            // Re-fit whenever the viewport itself is resized (window resize, GridSplitter drag,
            // tab becoming visible for the first time) - see _autoFit above for why this doesn't
            // just override a zoom/pan the user already made.
            SizeChanged += (_, _) =>
            {
                if (_autoFit)
                {
                    ZoomToFit();
                }
            };
        }

        public double ContentScale
        {
            get => (double)GetValue(ContentScaleProperty);
            set => SetValue(ContentScaleProperty, value);
        }

        public double MinContentScale
        {
            get => (double)GetValue(MinContentScaleProperty);
            set => SetValue(MinContentScaleProperty, value);
        }

        public double MaxContentScale
        {
            get => (double)GetValue(MaxContentScaleProperty);
            set => SetValue(MaxContentScaleProperty, value);
        }

        /// <summary>X offset of the content, in screen (post-scale) pixels.</summary>
        public double ContentOffsetX
        {
            get => (double)GetValue(ContentOffsetXProperty);
            set => SetValue(ContentOffsetXProperty, value);
        }

        /// <summary>Y offset of the content, in screen (post-scale) pixels.</summary>
        public double ContentOffsetY
        {
            get => (double)GetValue(ContentOffsetYProperty);
            set => SetValue(ContentOffsetYProperty, value);
        }

        public double ZoomStep
        {
            get => (double)GetValue(ZoomStepProperty);
            set => SetValue(ZoomStepProperty, value);
        }

        /// <summary>Resets scale to 1 and offset to (0,0).</summary>
        public void ResetView()
        {
            _autoFit = false; // "actual size" is a deliberate choice, not "fit" - a later resize shouldn't undo it.
            ContentScale = 1.0;
            ContentOffsetX = 0.0;
            ContentOffsetY = 0.0;
        }

        /// <summary>
        /// Scales/centers the content so it fits entirely within the current viewport, and
        /// resumes automatically re-fitting on every future resize (see _autoFit).
        /// </summary>
        public void ZoomToFit()
        {
            _autoFit = true;

            if (_content is null || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            double contentWidth = _content.ActualWidth;
            double contentHeight = _content.ActualHeight;
            if (contentWidth <= 0 || contentHeight <= 0)
            {
                return;
            }

            double newScale = Math.Min(ActualWidth / contentWidth, ActualHeight / contentHeight);
            ContentScale = newScale; // Coerced to [MinContentScale, MaxContentScale].
            ContentOffsetX = (ActualWidth - (contentWidth * ContentScale)) / 2.0;
            ContentOffsetY = (ActualHeight - (contentHeight * ContentScale)) / 2.0;
        }

        /// <summary>
        /// Zooms to <paramref name="newScale"/> while keeping the content point currently under
        /// <paramref name="viewportPoint"/> (control coordinates) fixed on screen - the same
        /// "zoom about the cursor" behaviour the mouse wheel uses, exposed for callers that want
        /// to drive it from e.g. a zoom-in/zoom-out toolbar button aimed at the viewport center.
        /// </summary>
        public void ZoomAboutPoint(double newScale, Point viewportPoint)
        {
            _autoFit = false;

            double oldScale = ContentScale;
            var contentPoint = new Point(
                (viewportPoint.X - ContentOffsetX) / oldScale,
                (viewportPoint.Y - ContentOffsetY) / oldScale);

            ContentScale = newScale; // Coerced to [MinContentScale, MaxContentScale] - read back below.

            ContentOffsetX = viewportPoint.X - (contentPoint.X * ContentScale);
            ContentOffsetY = viewportPoint.Y - (contentPoint.Y * ContentScale);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_content is not null)
            {
                _content.SizeChanged -= OnContentSizeChanged;
            }

            _content = GetTemplateChild("PART_Content") as FrameworkElement;
            if (_content is not null)
            {
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(_scaleTransform);
                transformGroup.Children.Add(_translateTransform);
                _content.RenderTransform = transformGroup;

                // The wrapped content is typically a Grid whose Width/Height are bound to a
                // freshly (re)generated heatmap/dome's own pixel dimensions (see MosaicView.xaml) -
                // re-fit whenever those change, not just when the viewport itself is resized.
                _content.SizeChanged += OnContentSizeChanged;
            }
        }

        private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_autoFit)
            {
                ZoomToFit();
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            if (_content is null)
            {
                return;
            }

            double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
            ZoomAboutPoint(ContentScale * factor, e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            if (_content is null)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ResetView();
                e.Handled = true;
                return;
            }

            _lastDragPoint = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_lastDragPoint is not Point lastPoint || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            _autoFit = false;

            Point currentPoint = e.GetPosition(this);
            ContentOffsetX += currentPoint.X - lastPoint.X;
            ContentOffsetY += currentPoint.Y - lastPoint.Y;
            _lastDragPoint = currentPoint;
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            EndDrag();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _lastDragPoint = null;
            ClearValue(CursorProperty);
        }

        private void EndDrag()
        {
            if (_lastDragPoint is null)
            {
                return;
            }

            _lastDragPoint = null;
            ClearValue(CursorProperty);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        private static void OnContentScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ZoomAndPanControl)d;
            control._scaleTransform.ScaleX = (double)e.NewValue;
            control._scaleTransform.ScaleY = (double)e.NewValue;
        }

        private static object CoerceContentScale(DependencyObject d, object baseValue)
        {
            var control = (ZoomAndPanControl)d;
            double value = (double)baseValue;
            return Math.Min(Math.Max(value, control.MinContentScale), control.MaxContentScale);
        }

        private static void OnContentOffsetXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ZoomAndPanControl)d)._translateTransform.X = (double)e.NewValue;

        private static void OnContentOffsetYChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ZoomAndPanControl)d)._translateTransform.Y = (double)e.NewValue;
    }
}
