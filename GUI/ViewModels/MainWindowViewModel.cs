using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading; // For Dispatcher
using CommunityToolkit.Mvvm.ComponentModel; // Zamiast ręcznej implementacji INotifyPropertyChanged
using CommunityToolkit.Mvvm.Input; // Dla RelayCommand
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets; // Dla enums
using MuOnlineConsole.Client;
using MuOnlineConsole.Configuration;
using MuOnlineConsole.Core.Models; // Dla ServerInfo
using MuOnlineConsole.Core.Utilities; // Dla klas baz danych

namespace MuOnlineConsole.GUI.ViewModels
{
    // Klasy pomocnicze dla list w UI
    public class ServerInfoViewModel
    {
        public ushort ServerId { get; set; }
        public byte LoadPercentage { get; set; }
        public string DisplayText => $"ID: {ServerId}, Load: {LoadPercentage}%";
    }

    public class CharacterInfoViewModel
    {
        public string Name { get; set; } = string.Empty;
        public CharacterClassNumber Class { get; set; }
        public string DisplayText => $"{Name} ({CharacterClassDatabase.GetClassName(Class)})";
    }

    // Główny ViewModel
    public partial class MainWindowViewModel : ObservableObject // Użyj ObservableObject z CommunityToolkit.Mvvm
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MainWindowViewModel> _logger;
        private readonly MuOnlineSettings _settings;
        private SimpleLoginClient? _client;
        private readonly CharacterState _characterState; // Przechowuj stan postaci
        private readonly ScopeManager _scopeManager; // Przechowuj menedżera zasięgu

        // --- Kolekcje dla UI ---
        [ObservableProperty] private ObservableCollection<string> _logMessages = new();
        [ObservableProperty] private ObservableCollection<ServerInfoViewModel> _serverList = new();
        [ObservableProperty] private ObservableCollection<CharacterInfoViewModel> _characterList = new();
        [ObservableProperty] private ObservableCollection<string> _scopeItems = new();
        [ObservableProperty] private ObservableCollection<string> _inventoryItems = new();
        [ObservableProperty] private ObservableCollection<string> _skillItems = new();
        [ObservableProperty] private ObservableCollection<KeyValuePair<string, string>> _characterStatsList = new(); // NOWA: Dla zakładki Stats

        // --- Kolekcja dla Mapy ---
        [ObservableProperty]
        private ObservableCollection<MapObjectViewModel> _mapObjects = new();
        // Słownik do szybkiego wyszukiwania obiektów na mapie po ID
        private readonly ConcurrentDictionary<ushort, MapObjectViewModel> _mapObjectDictionary = new();

        // --- Skalowanie Mapy ---
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MapWidth))] // Powiadom o zmianie MapWidth
        [NotifyPropertyChangedFor(nameof(MapHeight))] // Powiadom o zmianie MapHeight
        private double _mapScale = 1.0;
        [ObservableProperty] private double _mapOffsetX = 0;
        [ObservableProperty] private double _mapOffsetY = 0;
        private const double MaxMapScale = 30.0;
        private const double MinMapScale = 0.5; // Pozwól na mniejszą skalę
        private const double ScaleStep = 1.2;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        [NotifyPropertyChangedFor(nameof(ConnectionStatus))] // Dodaj to, jeśli ConnectionStatus zależy od _currentState
        [NotifyPropertyChangedFor(nameof(CharacterInfo))] // Dodaj to, jeśli CharacterInfo zależy od _currentState
        [NotifyPropertyChangedFor(nameof(CanConnectServer))] // Jeśli zależy od stanu
        [NotifyPropertyChangedFor(nameof(CanRefreshServers))] // Jeśli zależy od stanu
        [NotifyPropertyChangedFor(nameof(CanConnectGameServer))] // Jeśli zależy od stanu
        [NotifyPropertyChangedFor(nameof(CanSelectCharacter))] // Jeśli zależy od stanu
                                                               // Usunięto: [NotifyCanExecuteChangedFor(nameof(IsInGame))]
        private ClientConnectionState _currentState = ClientConnectionState.Initial;

        // Właściwość IsInGame pozostaje bez zmian lub ją dodaj, jeśli jej nie ma
        public bool IsInGame => CurrentState == ClientConnectionState.InGame;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectGameServerCommand))]
        private ServerInfoViewModel? _selectedServer;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SelectCharacterCommand))]
        private CharacterInfoViewModel? _selectedCharacter;

        [ObservableProperty]
        private string _inputText = string.Empty; // Tekst wprowadzany przez użytkownika

        // --- Właściwości Bindowane ---
        public string WindowTitle => $"MU Console Client - {(_characterState?.Name ?? "No Character")} ({CharacterClassDatabase.GetClassName(_characterState?.Class ?? CharacterClassNumber.DarkWizard)}) - {CurrentState}";
        public string ConnectionStatus => $"State: {CurrentState}";
        public string CharacterInfo => IsInGame ? $"Char: {_characterState.Name}, Lvl: {_characterState.Level} ({_characterState.MasterLevel}), Map: {MapDatabase.GetMapName(_characterState.MapId)} ({_characterState.PositionX},{_characterState.PositionY})" : "Not In Game";
        // CharacterStatsDisplay może pozostać dla prawej kolumny, jeśli chcesz
        public string CharacterStatsDisplay => IsInGame ? _characterState.GetStatsDisplay() : "N/A";

        public bool CanConnectServer => CurrentState == ClientConnectionState.Initial || CurrentState == ClientConnectionState.Disconnected;
        public bool CanRefreshServers => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanConnectGameServer => SelectedServer != null && CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanSelectCharacter => CurrentState == ClientConnectionState.ConnectedToGameServer && CharacterList.Any();
        public bool CanInteractWithServerList => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;

        [ObservableProperty]
        private bool _isAutoScrollEnabled = true; // Domyślnie włączony

        // Sygnał dla widoku, że powinien przewinąć (nie jest to standardowe MVVM, ale proste)
        public event EventHandler? ScrollToLogEndRequested;

        // NOWE: Właściwość dla Marginesu Canvas
        [ObservableProperty]
        private Thickness _canvasMargin = new Thickness(0);

        // Przywrócone: Właściwości dla rozmiaru Canvas
        public double MapWidth => 255 * MapScale;
        public double MapHeight => 255 * MapScale;

        private Size _currentMapContainerSize = new Size(); // Nadal potrzebne

        // Flaga projektowa dla XAML Designer
        public MainWindowViewModel() : this(null!, null!, true) { }

        public MainWindowViewModel(ILoggerFactory loggerFactory, MuOnlineSettings settings, bool designMode = false)
        {
            if (designMode)
            {
                // Załaduj przykładowe dane dla Designera
                _logMessages.Add("Designer Mode: Log message 1");
                _logMessages.Add("Designer Mode: Log message 2");
                _serverList.Add(new ServerInfoViewModel { ServerId = 0, LoadPercentage = 50 });
                _serverList.Add(new ServerInfoViewModel { ServerId = 1, LoadPercentage = 20 });
                _characterList.Add(new CharacterInfoViewModel { Name = "TestChar1", Class = CharacterClassNumber.BladeKnight });
                _characterList.Add(new CharacterInfoViewModel { Name = "TestChar2", Class = CharacterClassNumber.SoulMaster });
                _scopeItems.Add("ID: 12AB (Player: TestPlayer) at [100,120]");
                _scopeItems.Add("ID: C001 (NPC: Guard) at [110,115]");
                _inventoryItems.Add("Slot  12: Kris +15 +Skill +Luck +16 Opt (Dur: 20)");
                _skillItems.Add("ID: 6     Level: 1");
                _currentState = ClientConnectionState.ReceivedServerList; // Stan dla podglądu
                                                                          // Nie inicjuj _client ani _characterState w trybie projektowania
                _settings = new MuOnlineSettings(); // Użyj domyślnych lub pustych ustawień
                _logger = new LoggerFactory().CreateLogger<MainWindowViewModel>();
                _characterState = new CharacterState(new LoggerFactory()); // Utwórz pusty stan
                _scopeManager = new ScopeManager(new LoggerFactory(), _characterState);

                // Przykładowe dane dla zakładki Stats
                _characterStatsList.Add(new KeyValuePair<string, string>("HP", "100 / 100"));
                _characterStatsList.Add(new KeyValuePair<string, string>("Mana", "50 / 50"));
                _characterStatsList.Add(new KeyValuePair<string, string>("Strength", "25"));
                // Przykładowe dane dla mapy
                // _mapObjects.Add(new MapObjectViewModel { MapX = 120 * MapScale, MapY = 130 * MapScale, Color = Brushes.Yellow, ToolTipText = "Self @ (120,130)", Size = 8 });
                // _mapObjects.Add(new MapObjectViewModel { MapX = 125 * MapScale, MapY = 135 * MapScale, Color = Brushes.Red, ToolTipText = "OtherPlayer @ (125,135)" });
                // _mapObjects.Add(new MapObjectViewModel { MapX = 110 * MapScale, MapY = 110 * MapScale, Color = Brushes.LightBlue, ToolTipText = "Guard @ (110,110)" });

            }
            else
            {
                _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
                _logger = _loggerFactory.CreateLogger<MainWindowViewModel>();
                _settings = settings ?? throw new ArgumentNullException(nameof(settings));
                // CharacterState i ScopeManager są teraz tworzone tutaj
                _characterState = new CharacterState(_loggerFactory);
                _scopeManager = new ScopeManager(_loggerFactory, _characterState);
            }
        }


        /// <summary>
        /// Inicjalizuje i uruchamia SimpleLoginClient w osobnym wątku.
        /// </summary>
        public void InitializeClient()
        {
            _client = new SimpleLoginClient(_loggerFactory, _settings, this, _characterState, _scopeManager); // Przekaż ViewModel do klienta
            Task.Run(() => _client.RunAsync());
        }

        // --- Metody do aktualizacji UI z innych wątków ---
        public void UpdateConnectionState(ClientConnectionState newState)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentState == newState) return;
                bool oldIsInGame = IsInGame;
                SetProperty(ref _currentState, newState, nameof(CurrentState));

                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(ConnectionStatus));
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(IsInGame)); // Powiadom, jeśli stan gry się zmienił

                // Aktualizuj CanExecute dla WSZYSTKICH komend zależnych od stanu
                ConnectServerCommand.NotifyCanExecuteChanged();
                RefreshServersCommand.NotifyCanExecuteChanged();
                ConnectGameServerCommand.NotifyCanExecuteChanged();
                SelectCharacterCommand.NotifyCanExecuteChanged(); // WAŻNE: Aktualizuj CanExecute tutaj
                PickupNearestCommand.NotifyCanExecuteChanged();
                ShowStatsCommand.NotifyCanExecuteChanged();
                ShowInventoryCommand.NotifyCanExecuteChanged();
                ShowSkillsCommand.NotifyCanExecuteChanged();
                SendInputCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanInteractWithServerList)); // Aktualizuj też tę właściwość

                // Dodaj logowanie dostępnych komend w zależności od stanu
                switch (newState)
                {
                    case ClientConnectionState.ConnectedToGameServer:
                        // Stan po zalogowaniu, przed wyborem postaci
                        AddLogMessage("Connected to Game Server. Select a character using the 'Characters' tab and the 'Select Character' button.", LogLevel.Information);
                        break;
                    case ClientConnectionState.InGame:
                        // Stan po wejściu do gry
                        AddLogMessage("Entered game world. Available commands: scope, move, walk, walkto, pickup, stats, inv, skills, clearlog, exit.", LogLevel.Information);
                        break;
                    case ClientConnectionState.ReceivedServerList:
                        AddLogMessage("Server list received. Select a server in the 'Servers' tab and press 'Connect Game Server'. Or type 'refresh'.", LogLevel.Information);
                        break;
                    case ClientConnectionState.ConnectedToConnectServer:
                        // Informacja pojawia się już po połączeniu, ale można dodać przypomnienie
                        AddLogMessage("Connected to Connect Server. Waiting for server list or type 'refresh'.", LogLevel.Debug);
                        break;
                    case ClientConnectionState.Disconnected:
                        AddLogMessage("Disconnected. Use 'Connect Server' button to reconnect.", LogLevel.Warning);
                        break;
                }

                if (newState == ClientConnectionState.Disconnected)
                {
                    ServerList.Clear(); CharacterList.Clear(); ScopeItems.Clear();
                    InventoryItems.Clear(); SkillItems.Clear();
                }
            });
        }

        public void AddLogMessage(string message, LogLevel level = LogLevel.Information)
        {
            // Prosty przykład formatowania (można rozbudować)
            string prefix = level switch
            {
                LogLevel.Error => "[ERROR] ",
                LogLevel.Warning => "[WARN] ",
                LogLevel.Information => "[INFO] ",
                LogLevel.Debug => "[DEBUG] ",
                LogLevel.Trace => "[TRACE] ",
                _ => ""
            };

            string fullMessage = $"{DateTime.Now:HH:mm:ss.fff} {prefix}{message}";

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                const int maxLogMessages = 500;
                if (LogMessages.Count >= maxLogMessages) { LogMessages.RemoveAt(0); }
                LogMessages.Add(fullMessage);

                // Jeśli auto-scroll jest włączony, wywołaj zdarzenie
                if (IsAutoScrollEnabled)
                {
                    ScrollToLogEndRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        public void DisplayServerList(List<ServerInfo> servers)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               ServerList.Clear();
               foreach (var server in servers)
               {
                   ServerList.Add(new ServerInfoViewModel { ServerId = server.ServerId, LoadPercentage = server.LoadPercentage });
               }
               SelectedServer = null; // Wyczyść zaznaczenie
           });
        }

        public void DisplayCharacterList(List<(string Name, CharacterClassNumber Class)> characters)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterList.Clear();
                foreach (var character in characters)
                {
                    CharacterList.Add(new CharacterInfoViewModel { Name = character.Name, Class = character.Class });
                }
                SelectedCharacter = null; // Wyczyść zaznaczenie

                // Powiadom o zmianie CanExecute PO wypełnieniu listy
                SelectCharacterCommand.NotifyCanExecuteChanged();
                // Zaktualizuj też właściwość, od której zależy widoczność (jeśli używasz jej zamiast CanExecute)
                OnPropertyChanged(nameof(CanSelectCharacter));

                // Dodaj log po otrzymaniu listy
                if (characters.Any())
                {
                    AddLogMessage("Character list received. Select a character using the 'Characters' tab and the 'Select Character' button.", LogLevel.Information);
                }
                else
                {
                    AddLogMessage("Character list received, but no characters found on the account.", LogLevel.Warning);
                }

            });
        }

        public void UpdateCharacterStateDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Wywołaj OnPropertyChanged dla właściwości, które zależą od _characterState
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(IsInGame));

                // Wymuś aktualizację kolekcji (jeśli nie są automatycznie odświeżane)
                UpdateInventoryDisplay();
                UpdateSkillsDisplay();
                UpdateStatsDisplay(); // Jeśli masz oddzielną właściwość dla sformatowanych statystyk
            });
        }

        public void UpdateScopeDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               ScopeItems.Clear();
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Player)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Npc)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Monster)) ScopeItems.Add(item.ToString()); // Jeśli rozróżniasz
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Item)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Money)) ScopeItems.Add(item.ToString());
               // Sortuj dla czytelności?
               // var sortedItems = ScopeItems.OrderBy(s => s).ToList();
               // ScopeItems.Clear(); foreach(var s in sortedItems) ScopeItems.Add(s);
           });
        }

        public void UpdateInventoryDisplay()
        {
            // Upewnij się, że wywołujesz to w wątku UI
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                InventoryItems.Clear(); // Wyczyść starą listę
                InventoryItems.Add($"Zen: {_characterState.InventoryZen:N0}");
                InventoryItems.Add($"Expansion: {_characterState.InventoryExpansionState}");
                InventoryItems.Add("--- Items ---");

                // Pobierz aktualne itemy ze stanu postaci
                var currentItems = _characterState.GetInventoryItems();
                if (!currentItems.Any())
                {
                    InventoryItems.Add("(Inventory is empty)");
                }
                else
                {
                    // Iteruj po posortowanych slotach
                    foreach (var kvp in currentItems.OrderBy(kv => kv.Key))
                    {
                        // Użyj metody formatującej z CharacterState
                        string formattedItem = _characterState.FormatInventoryItem(kvp.Key, kvp.Value);
                        InventoryItems.Add(formattedItem);
                    }
                }
                InventoryItems.Add("-------------"); // Dodaj separator na końcu
            });
        }

        public void UpdateSkillsDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               SkillItems.Clear();
               foreach (var skill in _characterState.GetSkills())
               {
                   // TODO: Lookup skill name
                   SkillItems.Add($"ID: {skill.SkillId,-5} Lvl: {skill.SkillLevel,-2}");
               }
               if (!SkillItems.Any()) SkillItems.Add("(No skills)");
           });
        }

        public void UpdateStatsDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterStatsList.Clear();
                if (IsInGame)
                {
                    var stats = _characterState.GetFormattedStatsList();
                    foreach (var stat in stats)
                    {
                        CharacterStatsList.Add(stat);
                    }
                }
                // Powiadom też inne właściwości zależne od statystyk
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(CharacterStatsDisplay)); // Nadal aktualizuj podgląd w prawej kolumnie
            });
        }

        public void AddOrUpdateMapObject(ScopeObject scopeObject)
        {
            if (scopeObject == null) return;

            // Log BEFORE InvokeAsync
            _logger.LogDebug("--> VM.AddOrUpdate: Received ScopeObject ID={Id:X4}, Type={Type}, OriginalPos=({X},{Y}). Current MapScale={Scale:F2}",
                scopeObject.Id, scopeObject.ObjectType, scopeObject.PositionX, scopeObject.PositionY, MapScale);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                bool isSelf = scopeObject.Id == _characterState.Id;
                if (_mapObjectDictionary.TryGetValue(scopeObject.Id, out var existingMapObject))
                {
                    existingMapObject.UpdatePosition(scopeObject.PositionX, scopeObject.PositionY, MapScale);
                    if (isSelf) CalculateCanvasMargin(); // <--- PRZYWRÓCONE
                }
                else
                {
                    var newMapObject = CreateMapObjectViewModel(scopeObject);
                    if (newMapObject != null)
                    {
                        newMapObject.UpdatePosition(scopeObject.PositionX, scopeObject.PositionY, MapScale);
                        if (_mapObjectDictionary.TryAdd(scopeObject.Id, newMapObject))
                        {
                            MapObjects.Add(newMapObject);
                            if (isSelf) CalculateCanvasMargin(); // <--- PRZYWRÓCONE
                        }
                        else
                        {
                            _logger.LogWarning("    -> Failed to add MapObjectViewModel ID={Id:X4} to dictionary (already exists?).", scopeObject.Id);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("    -> Failed to create MapObjectViewModel for ID={Id:X4}", scopeObject.Id);
                    }
                }
            });
        }

        // Przywróć wywołania CalculateCanvasMargin
        partial void OnMapScaleChanged(double oldValue, double newValue)
        {
            OnPropertyChanged(nameof(MapWidth));
            OnPropertyChanged(nameof(MapHeight));
            UpdateAllMapObjectScales();
            CalculateCanvasMargin(); // <--- PRZYWRÓCONE
        }

        public void UpdateMapContainerSize(Size newSize) // Ta metoda pozostaje poprawna
        {
            if (_currentMapContainerSize != newSize && newSize.Width > 1 && newSize.Height > 1)
            {
                _logger.LogDebug("Updating MapContainerSize from {OldSize} to {NewSize}", _currentMapContainerSize, newSize);
                _currentMapContainerSize = newSize;
                RecalculateScaleOnly(); // To wywoła OnMapScaleChanged -> CalculateCanvasMargin
            }
        }

        // Przelicza tylko skalę
        private void RecalculateScaleOnly() // Ta metoda pozostaje poprawna
        {
            const double mapSizeInTiles = 255.0;
            if (_currentMapContainerSize.Width <= 1 || _currentMapContainerSize.Height <= 1 || mapSizeInTiles <= 0) { return; }
            double scaleX = _currentMapContainerSize.Width / mapSizeInTiles;
            double scaleY = _currentMapContainerSize.Height / mapSizeInTiles;
            double bestFitScale = Math.Max(MinMapScale, Math.Min(scaleX, scaleY));
            if (Math.Abs(MapScale - bestFitScale) > 0.01) { MapScale = bestFitScale; }
            else { _logger.LogTrace("Skipping MapScale update, change too small."); }
        }

        // === PRZYWRÓĆ METODĘ ===
        private void CalculateCanvasMargin()
        {
            if (_mapObjectDictionary.TryGetValue(_characterState.Id, out var playerObject) && _currentMapContainerSize != default(Size) && _currentMapContainerSize.Width > 0 && _currentMapContainerSize.Height > 0)
            {
                double playerMapX = playerObject.MapX;
                double playerMapY = playerObject.MapY;
                double viewportCenterX = _currentMapContainerSize.Width / 2.0;
                double viewportCenterY = _currentMapContainerSize.Height / 2.0;
                double marginLeft = viewportCenterX - playerMapX;
                double marginTop = viewportCenterY - playerMapY;

                _logger.LogDebug("Calculating Canvas Margin: PlayerMap=({pX:F2},{pY:F2}), ViewportCenter=({vX:F2},{vY:F2}), TargetMargin=({mL:F2},{mT:F2})",
                                 playerMapX, playerMapY, viewportCenterX, viewportCenterY, marginLeft, marginTop);

                CanvasMargin = new Thickness(marginLeft, marginTop, 0, 0);
            }
            else
            {
                _logger.LogTrace("Cannot calculate canvas margin: Player object (ID {PlayerId:X4}) not found or container size is zero/default.", _characterState.Id);
                CanvasMargin = new Thickness(0); // Ustaw domyślny margines
            }
        }
        // === KONIEC ===

        public void RemoveMapObject(ushort maskedId)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mapObjectDictionary.TryRemove(maskedId, out var mapObjectToRemove))
                {
                    MapObjects.Remove(mapObjectToRemove);
                }
            });
        }

        public void UpdateMapObjectPosition(ushort maskedId, byte x, byte y)
        {
            // Log BEFORE InvokeAsync
            _logger.LogDebug("--> VM.UpdatePosition: Received ID={Id:X4}, NewPos=({X},{Y}). Current MapScale={Scale:F2}",
                maskedId, x, y, MapScale);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mapObjectDictionary.TryGetValue(maskedId, out var mapObject))
                {
                    mapObject.UpdatePosition(x, y, MapScale);
                    if (maskedId == _characterState.Id) { CalculateCanvasMargin(); } // <--- PRZYWRÓCONE
                }
                else
                {
                    _logger.LogWarning("    -> MapObjectViewModel not found for ID {Id:X4} during position update.", maskedId);
                }
            });
        }

        public void ClearMapObjects(bool clearSelf = false)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (clearSelf || _characterState.Id == 0xFFFF)
                {
                    MapObjects.Clear();
                    _mapObjectDictionary.Clear();
                }
                else
                {
                    if (_mapObjectDictionary.TryGetValue(_characterState.Id, out var self))
                    {
                        MapObjects.Clear();
                        _mapObjectDictionary.Clear();
                        MapObjects.Add(self); // Dodaj siebie z powrotem
                        _mapObjectDictionary.TryAdd(self.Id, self);
                    }
                    else
                    {
                        MapObjects.Clear();
                        _mapObjectDictionary.Clear();
                    }
                }
            });
        }


        private MapObjectViewModel? CreateMapObjectViewModel(ScopeObject scopeObject)
        {
            string toolTipBase = "";
            IBrush color = Brushes.Gray;
            MapObjectType mapType = MapObjectType.Unknown;
            double size = 5;

            switch (scopeObject)
            {
                case PlayerScopeObject p:
                    if (p.Id == _characterState.Id) { mapType = MapObjectType.PlayerSelf; color = Brushes.Yellow; size = 8; toolTipBase = $"You ({p.Name})"; }
                    else { mapType = MapObjectType.PlayerOther; color = Brushes.White; toolTipBase = $"Player ({p.Name})"; }
                    break;
                case NpcScopeObject n:
                    mapType = MapObjectType.NpcQuest; color = Brushes.LightGreen; toolTipBase = NpcDatabase.GetNpcName(n.TypeNumber);
                    break;
                case ItemScopeObject i:
                    mapType = MapObjectType.Item; color = Brushes.Cyan; toolTipBase = i.ItemDescription; size = 3;
                    break;
                case MoneyScopeObject m:
                    mapType = MapObjectType.Money; color = Brushes.Gold; toolTipBase = $"Zen ({m.Amount})"; size = 3;
                    break;
                default:
                    _logger.LogWarning("Unknown scope object type for map: {Type}", scopeObject.GetType().Name);
                    return null;
            }

            // --- Use Constructor ---
            var viewModel = new MapObjectViewModel(
                id: scopeObject.Id,
                rawId: scopeObject.RawId,
                objectType: mapType,
                initialX: scopeObject.PositionX,
                initialY: scopeObject.PositionY
            );
            // --- End Constructor Use ---

            // Set remaining properties using standard assignment
            viewModel.Color = color;
            viewModel.Size = size;
            viewModel.ToolTipText = $"{toolTipBase} @ ({scopeObject.PositionX},{scopeObject.PositionY})";

            // Set initial MapX/MapY based on current scale AFTER object creation
            viewModel.MapX = scopeObject.PositionX * MapScale;
            viewModel.MapY = scopeObject.PositionY * MapScale;

            return viewModel;
        }

        // Komendy Zoom - zmodyfikuj, aby używały MinMapScale
        [RelayCommand]
        private void ZoomInMap()
        {
            MapScale = Math.Min(MapScale * ScaleStep, MaxMapScale);
            // OnMapScaleChanged zajmie się resztą (UpdateAllMapObjectScales + CalculateCanvasMargin)
            AddLogMessage($"Map zoomed in (Scale: {MapScale:F1})", LogLevel.Debug);
        }

        [RelayCommand]
        private void ZoomOutMap()
        {
            MapScale = Math.Max(MapScale / ScaleStep, MinMapScale); // Użyj MinMapScale
            // OnMapScaleChanged zajmie się resztą
            AddLogMessage($"Map zoomed out (Scale: {MapScale:F1})", LogLevel.Debug);
        }

        private void UpdateAllMapObjectScales() // Wywoływana przy zmianie MapScale
        {
            _logger.LogDebug("Updating all map object scales to: {NewScale:F2}", MapScale);
            foreach (var mapObj in _mapObjectDictionary.Values)
            {
                mapObj.UpdateScale(MapScale);
            }
            CalculateCanvasMargin(); // <--- PRZYWRÓCONE (lub upewnij się, że jest wywoływane przez OnMapScaleChanged)
        }

        // --- Komendy dla UI ---

        [RelayCommand(CanExecute = nameof(CanConnectServer))]
        private async Task ConnectServer()
        {
            AddLogMessage("Attempting to connect to Connect Server...");
            // Wywołaj metodę w kliencie, która rozpocznie połączenie
            if (_client != null) await _client.ConnectToConnectServerAsync(); // TODO: Adapt Client to allow reconnection
            else AddLogMessage("Client not initialized.", LogLevel.Error);
        }

        [RelayCommand(CanExecute = nameof(CanRefreshServers))]
        private async Task RefreshServers()
        {
            AddLogMessage("Refreshing server list...");
            if (_client != null) await _client.RequestServerList();
            else AddLogMessage("Client not initialized.", LogLevel.Error);
        }

        [RelayCommand(CanExecute = nameof(CanConnectGameServer))]
        private async Task ConnectGameServer()
        {
            if (SelectedServer != null)
            {
                AddLogMessage($"Connecting to Game Server ID {SelectedServer.ServerId}...");
                // Wywołaj metodę w kliencie, która zainicjuje przełączenie
                if (_client != null) await _client.HandleServerSelectionAsync(SelectedServer.ServerId); // TODO: Expose this or similar logic
                else AddLogMessage("Client not initialized.", LogLevel.Error);
            }
            else { AddLogMessage("No server selected.", LogLevel.Warning); }
        }

        [RelayCommand(CanExecute = nameof(CanSelectCharacter))]
        private async Task SelectCharacter()
        {
            if (SelectedCharacter != null)
            {
                AddLogMessage($"Selecting character: {SelectedCharacter.Name}...", LogLevel.Information);
                if (_client != null)
                {
                    // Użyj ProcessCommandAsync, aby zachować spójność obsługi komend
                    await _client.ProcessCommandAsync($"select {SelectedCharacter.Name}");
                }
                else AddLogMessage("Client not initialized.", LogLevel.Error);
            }
            else { AddLogMessage("No character selected.", LogLevel.Warning); }
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private async Task PickupNearest()
        {
            AddLogMessage("Attempting to pick up nearest item...", LogLevel.Information); // Loguj zamiar
            if (_client != null)
            {
                // Wywołaj publiczną metodę przetwarzania komend, symulując wpisanie "pickup near"
                await _client.ProcessCommandAsync("pickup near");
            }
            else
            {
                AddLogMessage("Client not initialized.", LogLevel.Error);
            }
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private void ShowStats()
        {
            AddLogMessage("--- Character Stats ---", LogLevel.Information);
            AddLogMessage(_characterState.GetStatsDisplay(), LogLevel.Information); // GetStatsDisplay już jest publiczne
            AddLogMessage("-----------------------", LogLevel.Information);

            // Aktualizuj dedykowaną właściwość, jeśli ją masz
            UpdateStatsDisplay();
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private void ShowInventory()
        {
            AddLogMessage("--- Inventory ---", LogLevel.Information); // Loguj do konsoli UI
            AddLogMessage(_characterState.GetInventoryDisplay(), LogLevel.Information);
            AddLogMessage("-----------------", LogLevel.Information);

            // Wywołaj aktualizację zakładki Inventory
            UpdateInventoryDisplay();
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private void ShowSkills()
        {
            AddLogMessage("--- Skill List ---", LogLevel.Information);
            AddLogMessage(_characterState.GetSkillListDisplay(), LogLevel.Information);
            AddLogMessage("------------------", LogLevel.Information);

            // Wywołaj aktualizację zakładki Skills
            UpdateSkillsDisplay();
        }


        [RelayCommand]
        private async Task SendInput(string? inputText)
        {
            if (!string.IsNullOrWhiteSpace(inputText))
            {
                AddLogMessage($"CMD> {inputText}"); // Dodaj polecenie do logu
                if (_client != null)
                {
                    // Wywołaj metodę przetwarzania komend w kliencie
                    await _client.ProcessCommandAsync(inputText); // TODO: Make ProcessCommandAsync public or create a new public method
                }
                else AddLogMessage("Client not initialized.", LogLevel.Error);
                InputText = string.Empty; // Wyczyść pole tekstowe po wysłaniu
            }
        }
    }
}