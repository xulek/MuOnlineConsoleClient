using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        // Kolekcje dla UI (użyj ObservableCollection dla automatycznych aktualizacji)
        [ObservableProperty] // Atrybut generujący właściwość z powiadomieniem
        private ObservableCollection<string> _logMessages = new();

        [ObservableProperty]
        private ObservableCollection<ServerInfoViewModel> _serverList = new();

        [ObservableProperty]
        private ObservableCollection<CharacterInfoViewModel> _characterList = new();

        [ObservableProperty]
        private ObservableCollection<string> _scopeItems = new(); // Przechowuje sformatowane stringi dla uproszczenia

        [ObservableProperty]
        private ObservableCollection<string> _inventoryItems = new(); // Przechowuje sformatowane stringi

        [ObservableProperty]
        private ObservableCollection<string> _skillItems = new(); // Przechowuje sformatowane stringi

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

        // Właściwości bindowane do UI
        public string WindowTitle => $"MU Console Client - {(_characterState?.Name ?? "No Character")} ({CharacterClassDatabase.GetClassName(_characterState?.Class ?? CharacterClassNumber.DarkWizard)}) - {CurrentState}";
        public string ConnectionStatus => $"State: {CurrentState}";
        public string CharacterInfo => IsInGame ? $"Char: {_characterState.Name}, Lvl: {_characterState.Level} ({_characterState.MasterLevel}), Map: {MapDatabase.GetMapName(_characterState.MapId)} ({_characterState.PositionX},{_characterState.PositionY})" : "Not In Game";
        public string CharacterStatsDisplay => _characterState != null && IsInGame
                                               ? _characterState.GetStatsDisplay()
                                               : "N/A (Not In Game)";
        public bool CanConnectServer => CurrentState == ClientConnectionState.Initial || CurrentState == ClientConnectionState.Disconnected;
        public bool CanRefreshServers => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanConnectGameServer => SelectedServer != null && CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanSelectCharacter => CurrentState == ClientConnectionState.ConnectedToGameServer && CharacterList.Any();
        public bool CanInteractWithServerList => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;

        [ObservableProperty]
        private bool _isAutoScrollEnabled = true; // Domyślnie włączony

        // Sygnał dla widoku, że powinien przewinąć (nie jest to standardowe MVVM, ale proste)
        public event EventHandler? ScrollToLogEndRequested;

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
            // Upewnij się, że działasz w wątku UI
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Powiadom UI, że wartość właściwości, do której jest zbindowany TextBlock, uległa zmianie.
                // UI ponownie odczyta jej wartość, wywołując getter, który pobierze aktualne dane z _characterState.GetStatsDisplay().
                OnPropertyChanged(nameof(CharacterStatsDisplay));

                // Możesz też tutaj zaktualizować tytuł okna, jeśli zawiera statystyki
                OnPropertyChanged(nameof(CharacterInfo)); // Jeśli CharacterInfo zawiera statystyki
                OnPropertyChanged(nameof(WindowTitle)); // Jeśli tytuł zawiera statystyki
            });
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