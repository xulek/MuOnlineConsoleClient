using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MuOnlineConsole.GUI.ViewModels;
using System;
using System.ComponentModel; // Potrzebne dla PropertyChangedEventArgs
using Avalonia.Controls.Primitives;
using Microsoft.Extensions.Logging;
using Avalonia; // Potrzebne dla IScrollable

namespace MuOnlineConsole.GUI.Views
{
    public partial class MainWindow : Window
    {
        private bool _isUserScrolling = false;
        private ScrollViewer? _logScrollViewer; // Przechowuj referencję

        public MainWindow()
        {
            InitializeComponent();
            this.DataContextChanged += MainWindow_DataContextChanged;

            // Znajdź ScrollViewer po załadowaniu kontrolki
            this.Loaded += (s, e) => // Użyj zdarzenia Loaded, aby mieć pewność, że kontrolki są dostępne
            {
                _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
                if (_logScrollViewer != null)
                {
                    // Subskrybuj do PropertyChanged dla właściwości Offset
                    _logScrollViewer.PropertyChanged += LogScrollViewer_PropertyChanged;
                }
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


        private void MainWindow_DataContextChanged(object? sender, EventArgs e)
        {
            if (this.DataContext is MainWindowViewModel oldVm)
            {
                oldVm.ScrollToLogEndRequested -= ViewModel_ScrollToLogEndRequested;
            }
            if (this.DataContext is MainWindowViewModel newVm)
            {
                newVm.ScrollToLogEndRequested += ViewModel_ScrollToLogEndRequested;
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