using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;

namespace InkToolbarTest
{
    /// <summary>
    ///     Ink Canvas, Drawing Canvas, and Canvas Control
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>
        /// This is the maximum Bitmap render size for Win2D
        /// </summary>
        const int MaxImageSize = 16384;

        #region Fields
        private readonly List<InkStrokeContainer> _strokes = new List<InkStrokeContainer>();

        private Flyout _eraseAllFlyout;

        private InkSynchronizer _inkSynchronizer;

        private bool _isErasing;

        private Point _lastPoint;

        private float displayDpi;
        #endregion

        #region Constructors
        public MainPage()
        {
            InitializeComponent();

            Loaded += MainPage_Loaded;
        }
        #endregion

        #region Methods

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Enable sharing
            DataTransferManager.GetForCurrentView().DataRequested += MainPage_DataRequested;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Disable sharing
            DataTransferManager.GetForCurrentView().DataRequested -= MainPage_DataRequested;
        }
        #endregion

        #region Implementation

        private async void MainPage_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();

            args.Request.Data.Properties.Title = "Ink";
            args.Request.Data.Properties.Description = "Ink image";

            await GetBitmapAsync(args.Request.Data);

            deferral.Complete();
        }

        private async Task GetBitmapAsync(DataPackage dataPackage)
        {
            var device = CanvasDevice.GetSharedDevice();

            using (var offscreen = new CanvasRenderTarget(
                device,
                Convert.ToSingle(DrawingCanvas.ActualWidth),
                Convert.ToSingle(DrawingCanvas.ActualHeight),
                96))
            {
                using (var session = offscreen.CreateDrawingSession())
                {
                    DrawInk(session);
                }

                var stream = new InMemoryRandomAccessStream();

                await offscreen.SaveAsync(stream, CanvasBitmapFileFormat.Bmp);

                // 1. Share as a bitmap
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));

                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    "ink.png",
                    CreationCollisionOption.GenerateUniqueName);

                using (var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    await offscreen.SaveAsync(fileStream, CanvasBitmapFileFormat.Png);
                }

                // 2. Share as a file
                dataPackage.SetStorageItems(new[] {file});

                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem);

                // 3. Share the thumbnail
                dataPackage.Properties.Thumbnail = RandomAccessStreamReference.CreateFromStream(thumbnail);
            }
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            var display = DisplayInformation.GetForCurrentView();

            display.DpiChanged += Display_DpiChanged;

            Display_DpiChanged(display, null);

            var maxSize = Math.Max(CanvasContainer.Width, CanvasContainer.Height);
            ScrollViewer.MaxZoomFactor = MaxImageSize / System.Convert.ToSingle(maxSize);

            // 1. Activate custom drawing 
            _inkSynchronizer = InkCanvas.InkPresenter.ActivateCustomDrying();

            // 2. add use custom drawing when strokes are collected
            InkCanvas.InkPresenter.StrokesCollected += InkPresenter_StrokesCollected;

            // 3. Get the eraser button to handle custom dry ink and replace the erase all button with new logic
            var eraser = InkToolbar.GetToolButton(InkToolbarTool.Eraser) as InkToolbarEraserButton;

            if (eraser != null)
            {
                eraser.Checked += Eraser_Checked;
                eraser.Unchecked += Eraser_Unchecked;
            }

            _eraseAllFlyout = FlyoutBase.GetAttachedFlyout(eraser) as Flyout;

            if (_eraseAllFlyout != null)
            {
                var button = _eraseAllFlyout.Content as Button;

                if (button != null)
                {
                    var newButton = new Button();
                    newButton.Style = button.Style;
                    newButton.Content = button.Content;

                    newButton.Click += EraseAllInk;
                    _eraseAllFlyout.Content = newButton;
                }
            }
        }

        /// <summary>
        /// Update the Scroll Viewer when the DPI changes
        /// </summary>
        /// <param name="sender">the display information</param>
        /// <param name="args">the arguments</param>
        /// <remarks>Adapted from Win2D Gallery Mandelbrot sample at
        /// <![CDATA[https://github.com/Microsoft/Win2D/blob/master/samples/ExampleGallery/Shared/Mandelbrot.xaml.cs]]>
        /// </remarks>
        private void Display_DpiChanged(DisplayInformation sender, object args)
        {
            displayDpi = sender.LogicalDpi;

            OnScrollViewerViewChanged(null, null);
        }

        private void EraseAllInk(object sender, RoutedEventArgs e)
        {
            _strokes.Clear();

            DrawingCanvas.Invalidate();

            _eraseAllFlyout.Hide();
        }

        private void Eraser_Checked(object sender, RoutedEventArgs e)
        {
            var unprocessedInput = InkCanvas.InkPresenter.UnprocessedInput;

            unprocessedInput.PointerPressed += UnprocessedInput_PointerPressed;
            unprocessedInput.PointerMoved += UnprocessedInput_PointerMoved;
            unprocessedInput.PointerReleased += UnprocessedInput_PointerReleased;
            unprocessedInput.PointerExited += UnprocessedInput_PointerExited;
            unprocessedInput.PointerLost += UnprocessedInput_PointerLost;

            InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;
        }

        private void Eraser_Unchecked(object sender, RoutedEventArgs e)
        {
            var unprocessedInput = InkCanvas.InkPresenter.UnprocessedInput;

            unprocessedInput.PointerPressed -= UnprocessedInput_PointerPressed;
            unprocessedInput.PointerMoved -= UnprocessedInput_PointerMoved;
            unprocessedInput.PointerReleased -= UnprocessedInput_PointerReleased;
            unprocessedInput.PointerExited -= UnprocessedInput_PointerExited;
            unprocessedInput.PointerLost -= UnprocessedInput_PointerLost;

            InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
        }

        private void UnprocessedInput_PointerMoved(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (!_isErasing)
            {
                return;
            }

            var invalidate = false;

            foreach (var item in _strokes.ToArray())
            {
                var rect = item.SelectWithLine(_lastPoint, args.CurrentPoint.Position);

                if (rect.IsEmpty)
                {
                    continue;
                }

                if (rect.Width*rect.Height > 0)
                {
                    _strokes.Remove(item);

                    invalidate = true;
                }
            }

            _lastPoint = args.CurrentPoint.Position;

            args.Handled = true;

            if (invalidate)
            {
                DrawingCanvas.Invalidate();
            }
        }

        private void UnprocessedInput_PointerLost(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (_isErasing)
            {
                args.Handled = true;
            }

            _isErasing = false;
        }

        private void UnprocessedInput_PointerExited(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (_isErasing)
            {
                args.Handled = true;
            }

            _isErasing = true;
        }

        private void UnprocessedInput_PointerPressed(InkUnprocessedInput sender, PointerEventArgs args)
        {
            _lastPoint = args.CurrentPoint.Position;

            args.Handled = true;

            _isErasing = true;
        }

        private void UnprocessedInput_PointerReleased(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (_isErasing)
            {
                args.Handled = true;
            }

            _isErasing = false;
        }

        private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            var strokes = _inkSynchronizer.BeginDry();

            var container = new InkStrokeContainer();

            container.AddStrokes(from item in strokes
                select item.Clone());

            _strokes.Add(container);

            _inkSynchronizer.EndDry();

            DrawingCanvas.Invalidate();
        }

        private void DrawCanvas(CanvasControl sender, CanvasDrawEventArgs args)
        {
            DrawInk(args.DrawingSession);
        }

        private void DrawInk(CanvasDrawingSession session)
        {
            session.Clear(DrawingCanvas.ClearColor);

            foreach (var item in _strokes)
            {
                var strokes = item.GetStrokes();

                using (var list = new CanvasCommandList(session))
                {
                    using (var listSession = list.CreateDrawingSession())
                    {
                        listSession.DrawInk(strokes);
                    }

                    using (var shadowEffect = new ShadowEffect
                    {
                        ShadowColor = Colors.DarkRed,
                        Source = list
                    })
                    {
                        session.DrawImage(shadowEffect, new Vector2(2, 2));
                    }
                }

                session.DrawInk(strokes);
            }
        }

        private async void OnShare(object sender, RoutedEventArgs e)
        {
            var activeTool = InkToolbar.ActiveTool;

            // Show the share UI
            DataTransferManager.ShowShareUI();

            await Task.Delay(TimeSpan.FromSeconds(0.1));

            // reset the active tool after pressing the share button
            InkToolbar.ActiveTool = activeTool;
        }

        /// <summary>
        /// When the ScrollViewer zooms in or out, we update DpiScale on our CanvasVirtualControl
        /// to match. This adjusts its pixel density to match the current zoom level. But its size
        /// in dips stays the same, so layout and scroll position are not affected by the zoom.        
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <remarks>Adapted from Win2D Gallery Mandelbrot sample at
        /// <![CDATA[https://github.com/Microsoft/Win2D/blob/master/samples/ExampleGallery/Shared/Mandelbrot.xaml.cs]]>
        /// </remarks>
        private void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Cancel out the display DPI, so our fractal always renders at 96 DPI regardless of display
            // configuration. This boosts performance on high DPI displays, at the cost of visual quality.
            // For even better performance (but lower quality) this value could be further reduced.
            float dpiAdjustment = 96 / displayDpi;

            // Adjust DPI to match the current zoom level.
            float dpiScale = dpiAdjustment * ScrollViewer.ZoomFactor;

            // To boost performance during pinch-zoom manipulations, we only update DPI when it has
            // changed by more than 20%, or at the end of the zoom (when e.IsIntermediate reports false).
            // Smaller changes will just scale the existing bitmap, which is much faster than recomputing
            // the fractal at a different resolution. To trade off between zooming perf vs. smoothness,
            // adjust the thresholds used in this ratio comparison.

            var ratio = DrawingCanvas.DpiScale / dpiScale;

            if (e == null || !e.IsIntermediate || ratio <= 0.8 || ratio >= 1.25)
            {
                DrawingCanvas.DpiScale = dpiScale;
            }
        }
        #endregion
    }
}