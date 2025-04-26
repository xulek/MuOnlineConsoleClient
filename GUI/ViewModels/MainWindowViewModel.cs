using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets;
using MuOnlineConsole.Client;
using MuOnlineConsole.Configuration;
using MuOnlineConsole.Core.Models;
using MuOnlineConsole.Core.Utilities;

namespace MuOnlineConsole.GUI.ViewModels
{
    /// <summary>
    /// View model for server information displayed in a list.
    /// </summary>
    public class ServerInfoViewModel
    {
        public ushort ServerId { get; set; }
        public byte LoadPercentage { get; set; }
        public string DisplayText => $"ID: {ServerId}, Load: {LoadPercentage}%";
    }

    /// <summary>
    /// View model for character information displayed in a list.
    /// </summary>
    public class CharacterInfoViewModel
    {
        public string Name { get; set; } = string.Empty;
        public CharacterClassNumber Class { get; set; }
        public string DisplayText => $"{Name} ({CharacterClassDatabase.GetClassName(Class)})";
    }

    /// <summary>
    /// The main view model for the application window.
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MainWindowViewModel> _logger;
        private readonly MuOnlineSettings _settings;
        private SimpleLoginClient? _client;
        private readonly CharacterState _characterState;
        private readonly ScopeManager _scopeManager;

        // Collections for UI binding
        [ObservableProperty] private ObservableCollection<string> _logMessages = new();
        [ObservableProperty] private ObservableCollection<ServerInfoViewModel> _serverList = new();
        [ObservableProperty] private ObservableCollection<CharacterInfoViewModel> _characterList = new();
        [ObservableProperty] private ObservableCollection<string> _scopeItems = new();
        [ObservableProperty] private ObservableCollection<string> _inventoryItems = new();
        [ObservableProperty] private ObservableCollection<string> _skillItems = new();
        [ObservableProperty] private ObservableCollection<KeyValuePair<string, string>> _characterStatsList = new();

        // Map related collections and properties
        [ObservableProperty] private ObservableCollection<MapObjectViewModel> _mapObjects = new();
        private readonly ConcurrentDictionary<ushort, MapObjectViewModel> _mapObjectDictionary = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MapWidth))]
        [NotifyPropertyChangedFor(nameof(MapHeight))]
        private double _mapScale = 2.1;
        [ObservableProperty] private double _mapOffsetX = 0;
        [ObservableProperty] private double _mapOffsetY = 0;
        private const double MaxMapScale = 30.0;
        private const double MinMapScale = 0.5;
        private const double ScaleStep = 1.2;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        [NotifyPropertyChangedFor(nameof(ConnectionStatus))]
        [NotifyPropertyChangedFor(nameof(CharacterInfo))]
        [NotifyPropertyChangedFor(nameof(CanConnectServer))]
        [NotifyPropertyChangedFor(nameof(CanRefreshServers))]
        [NotifyPropertyChangedFor(nameof(CanConnectGameServer))]
        [NotifyPropertyChangedFor(nameof(CanSelectCharacter))]
        private ClientConnectionState _currentState = ClientConnectionState.Initial;

        public bool IsInGame => CurrentState == ClientConnectionState.InGame;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectGameServerCommand))]
        private ServerInfoViewModel? _selectedServer;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SelectCharacterCommand))]
        private CharacterInfoViewModel? _selectedCharacter;

        [ObservableProperty]
        private string _inputText = string.Empty;

        // Bindable properties for UI display
        public string WindowTitle => $"MU Console Client - {_characterState?.Name ?? "No Character"} ({CharacterClassDatabase.GetClassName(_characterState?.Class ?? CharacterClassNumber.DarkWizard)}) - {CurrentState}";
        public string ConnectionStatus => $"State: {CurrentState}";
        public string CharacterInfo => IsInGame ? $"Char: {_characterState.Name}, Lvl: {_characterState.Level} ({_characterState.MasterLevel}), Map: {MapDatabase.GetMapName(_characterState.MapId)} ({_characterState.PositionX},{_characterState.PositionY})" : "Not In Game";
        public string CharacterStatsDisplay => IsInGame ? _characterState.GetStatsDisplay() : "N/A";

        public string CharHealthDisplay => IsInGame ? $"{_characterState.CurrentHealth}/{_characterState.MaximumHealth}" : "N/A";
        public double CharHealthPercentage => IsInGame && _characterState.MaximumHealth > 0 ? (_characterState.CurrentHealth / (double)_characterState.MaximumHealth) * 100 : 0;

        public string CharManaDisplay => IsInGame ? $"{_characterState.CurrentMana}/{_characterState.MaximumMana}" : "N/A";
        public double CharManaPercentage => IsInGame && _characterState.MaximumMana > 0 ? (_characterState.CurrentMana / (double)_characterState.MaximumMana) * 100 : 0;

        // Calculating experience percentage
        public string CharExperienceDisplay
        {
            get
            {
                if (!IsInGame || _characterState.ExperienceForNextLevel == 0) return "N/A";
                double percentage = (_characterState.Experience / (double)_characterState.ExperienceForNextLevel) * 100;
                return $"{percentage:F1}%"; // Format to one decimal place
            }
        }
        public double CharExperiencePercentage
        {
            get
            {
                if (!IsInGame || _characterState.ExperienceForNextLevel == 0) return 0;
                return (_characterState.Experience / (double)_characterState.ExperienceForNextLevel) * 100;
            }
        }

        public int CharStrength => IsInGame ? _characterState.Strength : 0;
        public int CharAgility => IsInGame ? _characterState.Agility : 0;
        public int CharVitality => IsInGame ? _characterState.Vitality : 0;
        public int CharEnergy => IsInGame ? _characterState.Energy : 0;
        public int CharCommand => IsInGame ? _characterState.Leadership : 0;
        public string CharShieldDisplay => IsInGame ? $"{_characterState.CurrentShield}/{_characterState.MaximumShield}" : "N/A";
        public string CharAbilityDisplay => IsInGame ? $"{_characterState.CurrentAbility}/{_characterState.MaximumAbility}" : "N/A";


        // CanExecute properties for commands
        public bool CanConnectServer => CurrentState == ClientConnectionState.Initial || CurrentState == ClientConnectionState.Disconnected;
        public bool CanRefreshServers => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanConnectGameServer => SelectedServer != null && CurrentState == ClientConnectionState.ReceivedServerList;
        public bool CanSelectCharacter => CurrentState == ClientConnectionState.ConnectedToGameServer && CharacterList.Any();
        public bool CanInteractWithServerList => CurrentState == ClientConnectionState.ConnectedToConnectServer || CurrentState == ClientConnectionState.ReceivedServerList;

        [ObservableProperty]
        private bool _isAutoScrollEnabled = true;

        // Event for requesting UI scroll
        public event EventHandler? ScrollToLogEndRequested;

        // Property for Canvas margin
        [ObservableProperty]
        private Thickness _canvasMargin = new Thickness(0);

        // Properties for Canvas size
        public double MapWidth => 255 * MapScale;
        public double MapHeight => 255 * MapScale;

        private const byte MaxMapCoordinate = 255;

        private Size _currentMapContainerSize = new Size();

        // Design time constructor
        public MainWindowViewModel() : this(null!, null!, true) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="settings">The application settings.</param>
        /// <param name="designMode">A flag indicating if the view model is in design mode.</param>
        public MainWindowViewModel(ILoggerFactory loggerFactory, MuOnlineSettings settings, bool designMode = false)
        {
            if (designMode)
            {
                // Load sample data for the designer
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
                _currentState = ClientConnectionState.ReceivedServerList;
                _settings = new MuOnlineSettings();
                _logger = new LoggerFactory().CreateLogger<MainWindowViewModel>();
                _characterState = new CharacterState(new LoggerFactory());
                _scopeManager = new ScopeManager(new LoggerFactory(), _characterState);

                _characterStatsList.Add(new KeyValuePair<string, string>("HP", "100 / 100"));
                _characterStatsList.Add(new KeyValuePair<string, string>("Mana", "50 / 50"));
                _characterStatsList.Add(new KeyValuePair<string, string>("Strength", "25"));
            }
            else
            {
                _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
                _logger = _loggerFactory.CreateLogger<MainWindowViewModel>();
                _settings = settings ?? throw new ArgumentNullException(nameof(settings));
                _characterState = new CharacterState(_loggerFactory);
                _scopeManager = new ScopeManager(_loggerFactory, _characterState);
            }
        }

        /// <summary>
        /// Initializes and starts the SimpleLoginClient in a separate task.
        /// </summary>
        public void InitializeClient()
        {
            _client = new SimpleLoginClient(_loggerFactory, _settings, this, _characterState, _scopeManager);
            Task.Run(() => _client.RunAsync());
        }

        // --- Methods to update UI from other threads ---

        /// <summary>
        /// Updates the connection state and notifies dependent properties and commands.
        /// </summary>
        /// <param name="newState">The new client connection state.</param>
        public void UpdateConnectionState(ClientConnectionState newState)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentState == newState) return;

                SetProperty(ref _currentState, newState, nameof(CurrentState));

                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(ConnectionStatus));
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(IsInGame));

                ConnectServerCommand.NotifyCanExecuteChanged();
                RefreshServersCommand.NotifyCanExecuteChanged();
                ConnectGameServerCommand.NotifyCanExecuteChanged();
                SelectCharacterCommand.NotifyCanExecuteChanged();
                PickupNearestCommand.NotifyCanExecuteChanged();
                ShowStatsCommand.NotifyCanExecuteChanged();
                ShowInventoryCommand.NotifyCanExecuteChanged();
                ShowSkillsCommand.NotifyCanExecuteChanged();
                SendInputCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanInteractWithServerList));

                switch (newState)
                {
                    case ClientConnectionState.ConnectedToGameServer:
                        AddLogMessage("Connected to Game Server. Select a character using the 'Characters' tab and the 'Select Character' button.", LogLevel.Information);
                        break;
                    case ClientConnectionState.InGame:
                        AddLogMessage("Entered game world. Available commands: scope, move, walk, walkto, pickup, stats, inv, skills, clearlog, exit.", LogLevel.Information);
                        break;
                    case ClientConnectionState.ReceivedServerList:
                        AddLogMessage("Server list received. Select a server in the 'Servers' tab and press 'Connect Game Server'. Or type 'refresh'.", LogLevel.Information);
                        break;
                    case ClientConnectionState.ConnectedToConnectServer:
                        AddLogMessage("Connected to Connect Server. Waiting for server list or type 'refresh'.", LogLevel.Debug);
                        break;
                    case ClientConnectionState.Disconnected:
                        AddLogMessage("Disconnected. Use 'Connect Server' button to reconnect.", LogLevel.Warning);
                        ServerList.Clear(); CharacterList.Clear(); ScopeItems.Clear();
                        InventoryItems.Clear(); SkillItems.Clear();
                        break;
                }
            });
        }

        /// <summary>
        /// Adds a log message to the UI log display.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The log level.</param>
        public void AddLogMessage(string message, LogLevel level = LogLevel.Information)
        {
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

                if (IsAutoScrollEnabled)
                {
                    ScrollToLogEndRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// Displays the list of servers in the UI.
        /// </summary>
        /// <param name="servers">The list of server information.</param>
        public void DisplayServerList(List<ServerInfo> servers)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               ServerList.Clear();
               foreach (var server in servers)
               {
                   ServerList.Add(new ServerInfoViewModel { ServerId = server.ServerId, LoadPercentage = server.LoadPercentage });
               }
               SelectedServer = null;
           });
        }

        /// <summary>
        /// Displays the list of characters in the UI.
        /// </summary>
        /// <param name="characters">The list of character name and class pairs.</param>
        public void DisplayCharacterList(List<(string Name, CharacterClassNumber Class)> characters)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterList.Clear();
                foreach (var character in characters)
                {
                    CharacterList.Add(new CharacterInfoViewModel { Name = character.Name, Class = character.Class });
                }
                SelectedCharacter = null;

                SelectCharacterCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanSelectCharacter));

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

        /// <summary>
        /// Updates the character state display properties.
        /// </summary>
        public void UpdateCharacterStateDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(IsInGame));

                UpdateInventoryDisplay();
                UpdateSkillsDisplay();
                UpdateStatsDisplay();
            });
        }

        /// <summary>
        /// Updates the scope items display in the UI.
        /// </summary>
        public void UpdateScopeDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               ScopeItems.Clear();
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Player)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Npc)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Monster)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Item)) ScopeItems.Add(item.ToString());
               foreach (var item in _scopeManager.GetScopeItems(ScopeObjectType.Money)) ScopeItems.Add(item.ToString());
           });
        }

        /// <summary>
        /// Updates the inventory items display in the UI.
        /// </summary>
        public void UpdateInventoryDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                InventoryItems.Clear();
                InventoryItems.Add($"Zen: {_characterState.InventoryZen:N0}");
                InventoryItems.Add($"Expansion: {_characterState.InventoryExpansionState}");
                InventoryItems.Add("--- Items ---");

                var currentItems = _characterState.GetInventoryItems();
                if (!currentItems.Any())
                {
                    InventoryItems.Add("(Inventory is empty)");
                }
                else
                {
                    foreach (var kvp in currentItems.OrderBy(kv => kv.Key))
                    {
                        string formattedItem = _characterState.FormatInventoryItem(kvp.Key, kvp.Value);
                        InventoryItems.Add(formattedItem);
                    }
                }
                InventoryItems.Add("-------------");
            });
        }

        /// <summary>
        /// Updates the skills display in the UI.
        /// </summary>
        public void UpdateSkillsDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
           {
               SkillItems.Clear();
               foreach (var skill in _characterState.GetSkills())
               {
                   SkillItems.Add($"ID: {skill.SkillId,-5} Lvl: {skill.SkillLevel,-2}");
               }
               if (!SkillItems.Any()) SkillItems.Add("(No skills)");
           });
        }

        /// <summary>
        /// Updates the character stats display in the UI.
        /// </summary>
        // --- Modification of the UpdateStatsDisplay Method ---
        public void UpdateStatsDisplay()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Update list for the Stats tab (remains unchanged)
                CharacterStatsList.Clear();
                if (IsInGame)
                {
                    var stats = _characterState.GetFormattedStatsList();
                    foreach (var stat in stats)
                    {
                        CharacterStatsList.Add(stat);
                    }
                }

                // !! IMPORTANT !!: Notify UI about the change of ALL properties used in the side panel
                OnPropertyChanged(nameof(CharacterInfo));
                OnPropertyChanged(nameof(WindowTitle));
                // OnPropertyChanged(nameof(CharacterStatsDisplay)); // No longer needed for the side panel

                // Notifications for new side panel properties
                OnPropertyChanged(nameof(CharHealthDisplay));
                OnPropertyChanged(nameof(CharHealthPercentage));
                OnPropertyChanged(nameof(CharManaDisplay));
                OnPropertyChanged(nameof(CharManaPercentage));
                OnPropertyChanged(nameof(CharExperienceDisplay));
                OnPropertyChanged(nameof(CharExperiencePercentage));
                OnPropertyChanged(nameof(CharStrength));
                OnPropertyChanged(nameof(CharAgility));
                OnPropertyChanged(nameof(CharVitality));
                OnPropertyChanged(nameof(CharEnergy));
                OnPropertyChanged(nameof(CharCommand));
                OnPropertyChanged(nameof(CharShieldDisplay));
                OnPropertyChanged(nameof(CharAbilityDisplay));
            });
        }

        /// <summary>
        /// Adds or updates a map object on the map display.
        /// </summary>
        /// <param name="scopeObject">The scope object to add or update.</param>
        public void AddOrUpdateMapObject(ScopeObject scopeObject)
        {
            if (scopeObject == null) return;

            _logger.LogDebug("--> VM.AddOrUpdate: Received ScopeObject ID={Id:X4}, Type={Type}, OriginalPos=({X},{Y}). Current MapScale={Scale:F2}",
                scopeObject.Id, scopeObject.ObjectType, scopeObject.PositionX, scopeObject.PositionY, MapScale);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                bool isSelf = scopeObject.Id == _characterState.Id;
                if (_mapObjectDictionary.TryGetValue(scopeObject.Id, out var existingMapObject))
                {
                    existingMapObject.UpdatePosition(scopeObject.PositionX, scopeObject.PositionY, MapScale);
                    if (isSelf) CalculateCanvasMargin();
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
                            if (isSelf) CalculateCanvasMargin();
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

        partial void OnMapScaleChanged(double oldValue, double newValue)
        {
            OnPropertyChanged(nameof(MapWidth));
            OnPropertyChanged(nameof(MapHeight));
            UpdateAllMapObjectScales();
            CalculateCanvasMargin();
        }

        /// <summary>
        /// Updates the size of the map container and recalculates the scale.
        /// </summary>
        /// <param name="newSize">The new size of the map container.</param>
        public void UpdateMapContainerSize(Size newSize)
        {
            if (_currentMapContainerSize != newSize && newSize.Width > 1 && newSize.Height > 1)
            {
                _logger.LogDebug("Updating MapContainerSize from {OldSize} to {NewSize}", _currentMapContainerSize, newSize);
                _currentMapContainerSize = newSize;
                RecalculateScaleOnly();
            }
        }

        /// <summary>
        /// Recalculates the map scale based on the current container size.
        /// </summary>
        private void RecalculateScaleOnly()
        {
            const double mapSizeInTiles = 255.0;
            if (_currentMapContainerSize.Width <= 1 || _currentMapContainerSize.Height <= 1 || mapSizeInTiles <= 0) { return; }
            double scaleX = _currentMapContainerSize.Width / mapSizeInTiles;
            double scaleY = _currentMapContainerSize.Height / mapSizeInTiles;
            double bestFitScale = Math.Max(MinMapScale, Math.Min(scaleX, scaleY));
            if (Math.Abs(MapScale - bestFitScale) > 0.01) { MapScale = bestFitScale; }
            else { _logger.LogTrace("Skipping MapScale update, change too small."); }
        }

        /// <summary>
        /// Calculates the canvas margin to keep the player centered on the map.
        /// </summary>
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
                CanvasMargin = new Thickness(0);
            }
        }

        /// <summary>
        /// Removes a map object from the display.
        /// </summary>
        /// <param name="maskedId">The masked ID of the object to remove.</param>
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

        /// <summary>
        /// Updates the position of a map object on the display.
        /// </summary>
        /// <param name="maskedId">The masked ID of the object.</param>
        /// <param name="x">The new X coordinate.</param>
        /// <param name="y">The new Y coordinate.</param>
        public void UpdateMapObjectPosition(ushort maskedId, byte x, byte y)
        {
            _logger.LogDebug("--> VM.UpdatePosition: Received ID={Id:X4}, NewPos=({X},{Y}). Current MapScale={Scale:F2}",
                maskedId, x, y, MapScale);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mapObjectDictionary.TryGetValue(maskedId, out var mapObject))
                {
                    mapObject.UpdatePosition(x, y, MapScale);
                    if (maskedId == _characterState.Id) { CalculateCanvasMargin(); }
                }
                else
                {
                    _logger.LogWarning("    -> MapObjectViewModel not found for ID {Id:X4} during position update.", maskedId);
                }
            });
        }

        /// <summary>
        /// Clears all map objects from the display. Optionally keeps the player object.
        /// </summary>
        /// <param name="clearSelf">If true, also removes the player object.</param>
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
                        MapObjects.Add(self);
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

        /// <summary>
        /// Creates a MapObjectViewModel from a ScopeObject.
        /// </summary>
        /// <param name="scopeObject">The scope object.</param>
        /// <returns>A new MapObjectViewModel or null if the type is unknown.</returns>
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

            var viewModel = new MapObjectViewModel(
                id: scopeObject.Id,
                rawId: scopeObject.RawId,
                objectType: mapType,
                initialX: scopeObject.PositionX,
                initialY: scopeObject.PositionY
            );

            viewModel.Color = color;
            viewModel.Size = size;
            viewModel.ToolTipText = $"{toolTipBase} @ ({scopeObject.PositionX},{scopeObject.PositionY})";

            viewModel.MapX = scopeObject.PositionX * MapScale;
            viewModel.MapY = (MaxMapCoordinate - scopeObject.PositionY) * MapScale;

            return viewModel;
        }

        /// <summary>
        /// Updates the scale for all map object view models.
        /// </summary>
        private void UpdateAllMapObjectScales()
        {
            _logger.LogDebug("Updating all map object scales to: {NewScale:F2}", MapScale);
            foreach (var mapObj in _mapObjectDictionary.Values)
            {
                mapObj.UpdateScale(MapScale);
            }
            CalculateCanvasMargin();
        }

        // --- Commands for UI interaction ---

        [RelayCommand]
        private void ZoomInMap()
        {
            MapScale = Math.Min(MapScale * ScaleStep, MaxMapScale);
            AddLogMessage($"Map zoomed in (Scale: {MapScale:F1})", LogLevel.Debug);
        }

        [RelayCommand]
        private void ZoomOutMap()
        {
            MapScale = Math.Max(MapScale / ScaleStep, MinMapScale);
            AddLogMessage($"Map zoomed out (Scale: {MapScale:F1})", LogLevel.Debug);
        }

        [RelayCommand(CanExecute = nameof(CanConnectServer))]
        private async Task ConnectServer()
        {
            AddLogMessage("Attempting to connect to Connect Server...");
            if (_client != null) await _client.ConnectToConnectServerAsync();
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
                if (_client != null) await _client.HandleServerSelectionAsync(SelectedServer.ServerId);
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
                    await _client.ProcessCommandAsync($"select {SelectedCharacter.Name}");
                }
                else AddLogMessage("Client not initialized.", LogLevel.Error);
            }
            else { AddLogMessage("No character selected.", LogLevel.Warning); }
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private async Task PickupNearest()
        {
            AddLogMessage("Attempting to pick up nearest item...", LogLevel.Information);
            if (_client != null)
            {
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
            AddLogMessage(_characterState.GetStatsDisplay(), LogLevel.Information);
            AddLogMessage("-----------------------", LogLevel.Information);

            UpdateStatsDisplay();
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private void ShowInventory()
        {
            AddLogMessage("--- Inventory ---", LogLevel.Information);
            AddLogMessage(_characterState.GetInventoryDisplay(), LogLevel.Information);
            AddLogMessage("-----------------", LogLevel.Information);

            UpdateInventoryDisplay();
        }

        [RelayCommand(CanExecute = nameof(IsInGame))]
        private void ShowSkills()
        {
            AddLogMessage("--- Skill List ---", LogLevel.Information);
            AddLogMessage(_characterState.GetSkillListDisplay(), LogLevel.Information);
            AddLogMessage("------------------", LogLevel.Information);

            UpdateSkillsDisplay();
        }

        [RelayCommand]
        private async Task SendInput(string? inputText)
        {
            if (!string.IsNullOrWhiteSpace(inputText))
            {
                AddLogMessage($"CMD> {inputText}");
                if (_client != null)
                {
                    await _client.ProcessCommandAsync(inputText);
                }
                else AddLogMessage("Client not initialized.", LogLevel.Error);
                InputText = string.Empty;
            }
        }
    }
}