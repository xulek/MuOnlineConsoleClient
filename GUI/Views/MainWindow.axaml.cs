using Avalonia; // Needed for Size, AvaloniaPropertyChangedEventArgs, BindingPriority
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MuOnlineConsole.GUI.ViewModels;
using Microsoft.Extensions.Logging; // Needed for ILogger

namespace MuOnlineConsole.GUI.Views
{
    /// <summary>
    /// The main application window.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Flag to track if the user is actively scrolling the log
        private bool _isUserScrolling = false;
        // Reference to the log ScrollViewer for programmatic scrolling
        private ScrollViewer? _logScrollViewer;
        // Optional logger instance
        private ILogger? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            // Hook DataContext changed event to manage ViewModel event subscriptions
            this.DataContextChanged += MainWindow_DataContextChanged;

            // Hook PropertyChanged event to handle window size changes
            this.PropertyChanged += (sender, e) =>
            {
                // Trigger map scale recalculation when ClientSize or DataContext changes
                // DataContext change is needed to ensure the ViewModel is available
                if ((e.Property.Name == nameof(ClientSize) || e.Property.Name == nameof(DataContext))
                    && DataContext is MainWindowViewModel vm) // Ensure ViewModel is set
                {
                    // Find the container holding the map Canvas
                    var mapContainer = this.FindControl<DockPanel>("MapDockPanel");

                    if (mapContainer == null)
                    {
                        // The control might not be available immediately when DataContext changes
                        _logger?.LogTrace("MapDockPanel not found yet during PropertyChanged for {Property}", e.Property.Name);
                        return;
                    }

                    // Use the actual bounds of the map container to calculate scale
                    var containerBounds = mapContainer.Bounds;
                    _logger?.LogDebug("Calculating MapScale based on MapDockPanel Bounds: {BoundsW}x{BoundsH}", containerBounds.Width, containerBounds.Height);

                    const double mapSizeInTiles = 255.0; // The map size in game tiles (e.g., 256x256, but coordinates are 0-255)

                    // Avoid division by zero or negative sizes
                    if (containerBounds.Width <= 1 || containerBounds.Height <= 1 || mapSizeInTiles <= 0)
                    {
                        _logger?.LogWarning("Invalid container bounds or mapSizeInTiles for scale calculation. Bounds: {W}x{H}", containerBounds.Width, containerBounds.Height);
                        vm.MapScale = 2.1; // Set a default minimum scale
                        return;
                    }

                    double scaleX = containerBounds.Width / mapSizeInTiles;
                    double scaleY = containerBounds.Height / mapSizeInTiles;

                    // Choose the minimum scale to fit the entire map, ensuring it's not too small
                    double bestFitScale = Math.Max(0.1, Math.Min(scaleX, scaleY));

                    _logger?.LogInformation("Recalculating MapScale: Container={W}x{H}, scaleX={sX:F2}, scaleY={sY:F2}, bestFitScale={bScale:F2}",
                        containerBounds.Width, containerBounds.Height, scaleX, scaleY, bestFitScale);

                    // Update the ViewModel's MapScale only if the change is significant
                    if (Math.Abs(vm.MapScale - bestFitScale) > 0.01)
                    {
                        vm.MapScale = bestFitScale; // This update triggers the VM's OnMapScaleChanged logic
                    }
                    else
                    {
                        _logger?.LogTrace("Skipping MapScale update, change too small ({Old:F2} -> {New:F2})", vm.MapScale, bestFitScale);
                    }
                }
            };

            // Hook Loaded event to find controls and perform post-load setup
            this.Loaded += (s, e) =>
            {
                _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
                if (_logScrollViewer != null)
                {
                    // Hook PropertyChanged on the ScrollViewer to detect manual scrolling
                    _logScrollViewer.PropertyChanged += LogScrollViewer_PropertyChanged;
                }

                // Trigger initial scale calculation after the window is loaded
                // This ensures the container bounds are available
                if (this.DataContext is MainWindowViewModel vm)
                {
                    // Use InvokeAsync to wait for layout pass before calculating scale
                    Dispatcher.UIThread.InvokeAsync(SimulateClientSizeChanged, DispatcherPriority.Background);
                }
            };
        }

        /// <summary>
        /// Handles PropertyChanged events for the log ScrollViewer, primarily to detect manual scrolling.
        /// </summary>
        private void LogScrollViewer_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Check if the changed property is the scroll Offset
            if (e.Property == ScrollViewer.OffsetProperty && sender is ScrollViewer scrollViewer)
            {
                var extent = scrollViewer.Extent; // Total scrollable content size
                var offset = scrollViewer.Offset; // Current scroll position
                var viewport = scrollViewer.Viewport; // Visible area size

                // Determine if the ScrollViewer is scrollable
                bool canScroll = extent.Height > viewport.Height;

                if (canScroll)
                {
                    // Check if the user has scrolled away from the bottom (more than a small margin)
                    if (offset.Y < extent.Height - viewport.Height - 5) // Use a small margin for floating point comparisons
                    {
                        if (!_isUserScrolling)
                        {
                            _isUserScrolling = true;
                            // If auto-scroll was enabled, disable it
                            if (DataContext is MainWindowViewModel vm && vm.IsAutoScrollEnabled)
                            {
                                vm.IsAutoScrollEnabled = false;
                                _logger?.LogTrace("AutoScroll disabled due to manual scroll up.");
                            }
                        }
                    }
                    // Check if the user has scrolled back to or near the bottom
                    else if (offset.Y >= extent.Height - viewport.Height - 5)
                    {
                        if (_isUserScrolling)
                        {
                            _logger?.LogTrace("User scrolled to bottom, auto-scroll can be re-enabled.");
                            _isUserScrolling = false; // Reset flag
                        }
                        // If auto-scroll is enabled and we are at the bottom, ensure the flag is false
                        if (DataContext is MainWindowViewModel vm && vm.IsAutoScrollEnabled)
                        {
                            _isUserScrolling = false;
                        }
                    }
                }
                else // Not scrollable, reset flag
                {
                    _isUserScrolling = false;
                }
            }
        }

        /// <summary>
        /// Manually triggers the ClientSize PropertyChanged logic to recalculate map scale.
        /// Useful for initial setup after layout is complete.
        /// </summary>
        private void SimulateClientSizeChanged()
        {
            // Trigger the PropertyChanged handler specifically for ClientSize
            this.OnPropertyChanged(new AvaloniaPropertyChangedEventArgs<Size>(this, ClientSizeProperty, default, this.ClientSize, Avalonia.Data.BindingPriority.Style));
        }

        /// <summary>
        /// Handles changes in the DataContext, primarily to subscribe/unsubscribe from ViewModel events.
        /// </summary>
        private void MainWindow_DataContextChanged(object? sender, EventArgs e)
        {
            // Unsubscribe from the event of the old ViewModel
            if (this.DataContext is MainWindowViewModel oldVm)
            {
                oldVm.ScrollToLogEndRequested -= ViewModel_ScrollToLogEndRequested;
            }

            // Subscribe to the event of the new ViewModel
            if (this.DataContext is MainWindowViewModel newVm)
            {
                newVm.ScrollToLogEndRequested += ViewModel_ScrollToLogEndRequested;

                // Trigger a scale calculation when the DataContext changes
                Dispatcher.UIThread.InvokeAsync(SimulateClientSizeChanged, DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Handles the ScrollToLogEndRequested event from the ViewModel to scroll the log to the end.
        /// Only scrolls if auto-scroll is enabled and the user hasn't manually scrolled up.
        /// </summary>
        private void ViewModel_ScrollToLogEndRequested(object? sender, EventArgs e)
        {
            // Ensure scrolling happens on the UI thread
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Scroll only if the ScrollViewer exists, auto-scroll is enabled in the VM, and the user is not manually scrolling
                if (_logScrollViewer != null && (sender as MainWindowViewModel)?.IsAutoScrollEnabled == true && !_isUserScrolling)
                {
                    _logScrollViewer.ScrollToEnd();
                    // The _isUserScrolling flag is managed by the ScrollViewer_PropertyChanged handler
                }
                else
                {
                    _logger?.LogTrace("AutoScroll requested but suppressed (ScrollViewer null, AutoScroll off, or user scrolling).");
                }
            });
        }

        /// <summary>
        /// Sets the logger instance for this window.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles the KeyDown event for the input TextBox to send the command on Enter key press.
        /// </summary>
        private void InputTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Check if DataContext is the ViewModel and the SendInputCommand can be executed
                if (DataContext is MainWindowViewModel viewModel && viewModel.SendInputCommand.CanExecute(viewModel.InputText))
                {
                    // Execute the command with the current text in the TextBox
                    viewModel.SendInputCommand.Execute(viewModel.InputText);
                    // The ViewModel is responsible for clearing InputText if needed
                }
                e.Handled = true; // Mark the event as handled to prevent other controls from processing it
            }
        }
    }
}