using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MuOnlineConsole.GUI.ViewModels;
using Microsoft.Extensions.Logging;
using Avalonia; // Potrzebne dla IScrollable

namespace MuOnlineConsole.GUI.Views
{
    public partial class MainWindow : Window
    {
        private bool _isUserScrolling = false;
        private ScrollViewer? _logScrollViewer; // Przechowuj referencję

        // Constructor (DataContextChanged, Loaded handler remain)
        public MainWindow()
        {
            InitializeComponent();
            this.DataContextChanged += MainWindow_DataContextChanged;

            // --- MODIFIED PropertyChanged for Scaling ---
            this.PropertyChanged += (sender, e) =>
            {
                // Handle ClientSize change OR DataContext change (to set initial scale)
                if ((e.Property.Name == nameof(ClientSize) || e.Property.Name == nameof(DataContext))
                    && DataContext is MainWindowViewModel vm && vm != null) // Ensure VM exists
                {
                    // Find the named container for the map
                    var mapContainer = this.FindControl<DockPanel>("MapDockPanel");

                    if (mapContainer == null)
                    {
                        // This might happen if the event fires before the control is ready
                        // or if the DataContext changes when the tab isn't visible.
                        _logger?.LogTrace("MapDockPanel not found yet during PropertyChanged for {Property}", e.Property.Name);
                        return; // Wait for the control to be available
                    }

                    // Use the container's bounds
                    var containerBounds = mapContainer.Bounds;
                    _logger?.LogDebug("Calculating MapScale based on MapDockPanel Bounds: {BoundsW}x{BoundsH}", containerBounds.Width, containerBounds.Height);

                    const double mapSizeInTiles = 255.0; // Use double for calculations

                    // Prevent division by zero or negative numbers
                    if (containerBounds.Width <= 1 || containerBounds.Height <= 1 || mapSizeInTiles <= 0)
                    {
                        _logger?.LogWarning("Invalid container bounds or mapSizeInTiles for scale calculation. Bounds: {W}x{H}", containerBounds.Width, containerBounds.Height);
                        // Optionally set a default minimum scale?
                        vm.MapScale = 1.0;
                        return;
                    }

                    double scaleX = containerBounds.Width / mapSizeInTiles;
                    double scaleY = containerBounds.Height / mapSizeInTiles;

                    // Use Math.Max with a small positive value to avoid zero scale
                    double bestFitScale = Math.Max(0.1, Math.Min(scaleX, scaleY)); // Ensure scale is at least slightly positive

                    _logger?.LogInformation("Recalculating MapScale: Container={W}x{H}, scaleX={sX:F2}, scaleY={sY:F2}, bestFitScale={bScale:F2}",
                        containerBounds.Width, containerBounds.Height, scaleX, scaleY, bestFitScale);

                    // Only update if the scale actually changes significantly to avoid excessive updates
                    if (Math.Abs(vm.MapScale - bestFitScale) > 0.01)
                    {
                        vm.MapScale = bestFitScale; // This triggers OnMapScaleChanged in VM
                    }
                    else
                    {
                        _logger?.LogTrace("Skipping MapScale update, change too small ({Old:F2} -> {New:F2})", vm.MapScale, bestFitScale);
                    }
                }
            };
            // --- END MODIFIED PropertyChanged ---


            this.Loaded += (s, e) =>
            {
                _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
                if (_logScrollViewer != null)
                {
                    _logScrollViewer.PropertyChanged += LogScrollViewer_PropertyChanged;
                }
                // --- ADD: Trigger initial scale calculation on Loaded ---
                // Ensure DataContext is set before triggering
                if (this.DataContext is MainWindowViewModel vm)
                {
                    // Manually trigger a calculation after load, as ClientSize might be stable now
                    SimulateClientSizeChanged();
                }
                // --- END ADD ---
            };
        }

        private void LogScrollViewer_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Sprawdź, czy zmieniona właściwość to Offset
            if (e.Property == ScrollViewer.OffsetProperty && sender is ScrollViewer scrollViewer)
            {
                // Logika wykrywania ręcznego przewijania (pozostaje podobna)
                var extent = scrollViewer.Extent;
                var offset = scrollViewer.Offset;
                var viewport = scrollViewer.Viewport;

                // Sprawdź, czy ScrollViewer jest przewijalny i czy nie jest na samym dole
                if (extent.Height > viewport.Height && offset.Y < extent.Height - viewport.Height - 5) // Mały margines błędu
                {
                    // Użytkownik prawdopodobnie przewinął w górę
                    if (!_isUserScrolling)
                    {
                        _isUserScrolling = true;
                        if (DataContext is MainWindowViewModel vm && vm.IsAutoScrollEnabled)
                        {
                            // Odznacz CheckBox w ViewModelu
                            vm.IsAutoScrollEnabled = false;
                            _logger?.LogTrace("AutoScroll disabled due to manual scroll up."); // Dodaj log, jeśli masz loggera
                        }
                    }
                }
                else if (extent.Height > viewport.Height && offset.Y >= extent.Height - viewport.Height - 5)
                {
                    // Użytkownik jest na dole lub blisko niego
                    if (_isUserScrolling)
                    {
                        _logger?.LogTrace("User scrolled to bottom, auto-scroll can be re-enabled."); // Dodaj log, jeśli masz loggera
                        _isUserScrolling = false; // Resetuj flagę
                    }
                }
                else // Nieprzewijalne lub na samej górze? W każdym razie nie jest to przewinięcie w górę od dołu.
                {
                    if (_isUserScrolling) _isUserScrolling = false; // Resetuj flagę, jeśli stan się zmienił
                }
            }
        }

        // --- ADD HELPER METHOD ---
        private void SimulateClientSizeChanged()
        {
            // This forces the PropertyChanged handler logic to run
            this.OnPropertyChanged(new AvaloniaPropertyChangedEventArgs<Size>(this, ClientSizeProperty, default, this.ClientSize, Avalonia.Data.BindingPriority.Style));
        }
        // --- END HELPER METHOD ---

        private void MainWindow_DataContextChanged(object? sender, EventArgs e)
        {
            if (this.DataContext is MainWindowViewModel oldVm) { oldVm.ScrollToLogEndRequested -= ViewModel_ScrollToLogEndRequested; }
            if (this.DataContext is MainWindowViewModel newVm)
            {
                newVm.ScrollToLogEndRequested += ViewModel_ScrollToLogEndRequested;
                // --- ADD: Trigger scale calc when DataContext changes ---
                Dispatcher.UIThread.InvokeAsync(SimulateClientSizeChanged, DispatcherPriority.Background); // Use InvokeAsync to ensure layout is likely done
                // --- END ADD ---
            }
        }

        private void ViewModel_ScrollToLogEndRequested(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Użyj zapisanej referencji _logScrollViewer
                if (_logScrollViewer != null && ((sender as MainWindowViewModel)?.IsAutoScrollEnabled ?? false)) // Przewijaj tylko jeśli AutoScroll włączony
                {
                    if (!_isUserScrolling) // Dodatkowy warunek - nie przewijaj, jeśli user właśnie przewijał ręcznie w górę
                    {
                        _logScrollViewer.ScrollToEnd();
                        // _isUserScrolling = false; // ScrollToEnd może nie resetować flagi, robimy to w PropertyChanged
                    }
                    else
                    {
                        _logger?.LogTrace("AutoScroll requested but suppressed by manual scroll flag."); // Dodaj log, jeśli masz loggera
                    }
                }
            });
        }

        // Logger (opcjonalny, ale przydatny do debugowania)
        private ILogger? _logger;
        // Metoda do ustawienia loggera, jeśli używasz DI lub przekazujesz z App.xaml.cs
        public void SetLogger(ILogger logger)
        {
            _logger = logger;
        }


        private void InputTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is MainWindowViewModel viewModel && viewModel.SendInputCommand.CanExecute(viewModel.InputText))
                {
                    viewModel.SendInputCommand.Execute(viewModel.InputText);
                    // viewModel.InputText = string.Empty; // Opcjonalne czyszczenie
                }
                e.Handled = true;
            }
        }
    }
}