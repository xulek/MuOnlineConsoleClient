using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ClientToServer; // Potrzebne dla PacketBuilder
using MUnique.OpenMU.Network.Packets.ConnectServer;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using MuOnlineConsole.Configuration;
using MuOnlineConsole.Core.Models;
using MuOnlineConsole.Core.Utilities;
using MuOnlineConsole.GUI.ViewModels; // Dodano using dla ViewModelu
using MuOnlineConsole.Networking;
using MuOnlineConsole.Networking.PacketHandling;
using MuOnlineConsole.Networking.Services;
using MUnique.OpenMU.Network; // Dla IConnection

// Upewnij się, że ta przestrzeń nazw jest poprawna i zawiera definicje enumów
namespace MuOnlineConsole.Client
{
    public sealed class SimpleLoginClient : IAsyncDisposable
    {
        private static readonly SimpleModulusKeys EncryptKeys = PipelinedSimpleModulusEncryptor.DefaultClientKey;
        private static readonly SimpleModulusKeys DecryptKeys = PipelinedSimpleModulusDecryptor.DefaultClientKey;
        private static readonly byte[] Xor3Keys = DefaultKeys.Xor3Keys;

        private readonly ILogger<SimpleLoginClient> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConnectionManager _connectionManager;
        private readonly LoginService _loginService;
        private readonly CharacterService _characterService;
        private readonly ConnectServerService _connectServerService;
        private readonly PacketRouter _packetRouter;
        private readonly MuOnlineSettings _settings;
        private readonly CharacterState _characterState;
        private readonly ScopeManager _scopeManager;
        private readonly MainWindowViewModel _viewModel;

        private ClientConnectionState _currentState = ClientConnectionState.Initial;
        private List<ServerInfo> _serverList = new();
        private List<(string Name, CharacterClassNumber Class)>? _pendingCharacterSelection = null;
        private bool _isWalking = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Dictionary<byte, byte> _serverDirectionMap;

        public MainWindowViewModel ViewModel => _viewModel;
        public bool IsInGame => _characterState.IsInGame;
        public ushort GetCharacterId() => _characterState.Id;
        public string GetCharacterName() => _characterState.Name;
        public bool IsConnected => _connectionManager.IsConnected;
        public bool LastPickupSucceeded { get; set; } = false;
        public bool PickupHandled { get; set; } = false;


        public SimpleLoginClient(ILoggerFactory loggerFactory, MuOnlineSettings settings, MainWindowViewModel viewModel, CharacterState characterState, ScopeManager scopeManager)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SimpleLoginClient>();
            _settings = settings;
            _viewModel = viewModel;
            _characterState = characterState;
            _scopeManager = scopeManager;

            var clientVersionBytes = Encoding.ASCII.GetBytes(settings.ClientVersion);
            var clientSerialBytes = Encoding.ASCII.GetBytes(settings.ClientSerial);
            // Odczytaj enum z przestrzeni nazw MuOnlineConsole.Client
            var targetVersion = System.Enum.Parse<TargetProtocolVersion>(settings.ProtocolVersion, ignoreCase: true);

            // Inicjalizacja serwisów i routera
            _connectionManager = new ConnectionManager(loggerFactory, EncryptKeys, DecryptKeys);
            _loginService = new LoginService(_connectionManager, _loggerFactory.CreateLogger<LoginService>(), clientVersionBytes, clientSerialBytes, Xor3Keys);
            _characterService = new CharacterService(_connectionManager, _loggerFactory.CreateLogger<CharacterService>());
            _connectServerService = new ConnectServerService(_connectionManager, _loggerFactory.CreateLogger<ConnectServerService>());

            // Przekaż 'this', aby handlery miały dostęp do ViewModelu i innych metod klienta
            _packetRouter = new PacketRouter(loggerFactory, _characterService, _loginService, targetVersion, this, _characterState, _scopeManager, _settings);

            _serverDirectionMap = settings.DirectionMap;

            _viewModel.UpdateConnectionState(_currentState);
        }

        public async Task RunAsync()
        {
            _viewModel.AddLogMessage("🚀 Starting client execution (GUI Mode)...", LogLevel.Information);
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // Czekaj na anulowanie
            }
            catch (OperationCanceledException)
            {
                _viewModel.AddLogMessage("🛑 Main execution loop cancelled.", LogLevel.Information);
            }
            catch (Exception ex)
            {
                _viewModel.AddLogMessage($"💥 Unexpected error in main execution loop: {ex.Message}", LogLevel.Error);
                _logger.LogError(ex, "💥 Unexpected error in main execution loop.");
            }
            finally
            {
                _viewModel.AddLogMessage("Shutting down client...", LogLevel.Information);
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
        }

        public async Task ConnectToConnectServerAsync()
        {
            if (_connectionManager.IsConnected)
            {
                _viewModel.AddLogMessage("🔌 Already connected. Disconnect first.", LogLevel.Warning);
                return;
            }

            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            if (cancellationToken.IsCancellationRequested)
            {
                _viewModel.AddLogMessage("🚫 Connection attempt cancelled before starting.", LogLevel.Warning);
                return;
            }

            _currentState = ClientConnectionState.ConnectingToConnectServer;
            _viewModel.UpdateConnectionState(_currentState);
            _viewModel.AddLogMessage($"🔌 Attempting connection to Connect Server {_settings.ConnectServerHost}:{_settings.ConnectServerPort}...", LogLevel.Information);
            _packetRouter.SetRoutingMode(true);

            if (await _connectionManager.ConnectAsync(_settings.ConnectServerHost, _settings.ConnectServerPort, false, cancellationToken))
            {
                var csConnection = _connectionManager.Connection;
                if (csConnection != null)
                {
                    // NAJPIERW ustaw stan i zaktualizuj UI
                    _currentState = ClientConnectionState.ConnectedToConnectServer;
                    _viewModel.UpdateConnectionState(_currentState);
                    _viewModel.AddLogMessage("✅ Successfully connected to Connect Server. Listener starting...", LogLevel.Information); // Zmień trochę log

                    // Następnie zasubskrybuj eventy
                    csConnection.PacketReceived += HandlePacketAsync;
                    csConnection.Disconnected += HandleDisconnectAsync;

                    // Na końcu uruchom nasłuchiwanie
                    _connectionManager.StartReceiving(cancellationToken);

                    // NIE wywołuj tutaj RequestServerList
                }
                else
                {
                    _viewModel.AddLogMessage("❌ Connection to CS succeeded but connection object is null.", LogLevel.Error);
                    _currentState = ClientConnectionState.Disconnected;
                    _viewModel.UpdateConnectionState(_currentState);
                    _cancellationTokenSource?.Cancel();
                }
            }
            else
            {
                _viewModel.AddLogMessage("❌ Connection to Connect Server failed.", LogLevel.Error);
                _currentState = ClientConnectionState.Disconnected;
                _viewModel.UpdateConnectionState(_currentState);
                _cancellationTokenSource?.Cancel();
            }
        }

        public void StoreServerList(List<ServerInfo> servers)
        {
            _serverList = servers;
            _currentState = ClientConnectionState.ReceivedServerList;
            _viewModel.UpdateConnectionState(_currentState);
            _viewModel.DisplayServerList(servers);
            _viewModel.AddLogMessage($"📝 Server list received with {servers.Count} servers. Select a server in the UI.", LogLevel.Information);
        }

        public async Task HandleServerSelectionAsync(ushort serverId)
        {
            if (_currentState != ClientConnectionState.ReceivedServerList)
            {
                _viewModel.AddLogMessage($"⚠️ Cannot select server {serverId}, not in the correct state ({_currentState}).", LogLevel.Warning);
                return;
            }

            _viewModel.AddLogMessage($"👉 Requesting connection details for Server ID {serverId}...", LogLevel.Information);
            _currentState = ClientConnectionState.RequestingConnectionInfo;
            _viewModel.UpdateConnectionState(_currentState);
            await _connectServerService.RequestConnectionInfoAsync(serverId);
        }

        public async void SwitchToGameServer(string host, int port)
        {
            if (_currentState != ClientConnectionState.RequestingConnectionInfo && _currentState != ClientConnectionState.ReceivedConnectionInfo)
            {
                _viewModel.AddLogMessage($"⚠️ Received game server info {host}:{port} in unexpected state ({_currentState}). Ignoring.", LogLevel.Warning);
                return;
            }
            _currentState = ClientConnectionState.ReceivedConnectionInfo;
            _viewModel.UpdateConnectionState(_currentState);

            _viewModel.AddLogMessage("🔌 Disconnecting from Connect Server...", LogLevel.Information);

            var oldConnection = _connectionManager.Connection;
            if (oldConnection != null)
            {
                try { oldConnection.PacketReceived -= HandlePacketAsync; } catch { /* Ignore */ }
                try { oldConnection.Disconnected -= HandleDisconnectAsync; } catch { /* Ignore */ }
            }

            await _connectionManager.DisconnectAsync();

            _viewModel.AddLogMessage($"🔌 Connecting to Game Server {host}:{port}...", LogLevel.Information);
            _currentState = ClientConnectionState.ConnectingToGameServer;
            _viewModel.UpdateConnectionState(_currentState);
            _packetRouter.SetRoutingMode(false);

            if (await _connectionManager.ConnectAsync(host, port, true, _cancellationTokenSource?.Token ?? default))
            {
                var newConnection = _connectionManager.Connection;
                if (newConnection != null)
                {
                    newConnection.PacketReceived += HandlePacketAsync;
                    newConnection.Disconnected += HandleDisconnectAsync;
                    _connectionManager.StartReceiving(_cancellationTokenSource?.Token ?? default);
                    // ---> POPRAWKA STANU TUTAJ <---
                    _currentState = ClientConnectionState.ConnectedToGameServer; // Ustaw stan Connected, czekaj na F1 00
                    _viewModel.UpdateConnectionState(_currentState);
                    _viewModel.AddLogMessage("✅ Connected to Game Server. Waiting for welcome packet...", LogLevel.Information);
                    // NIE wywołuj SendLoginRequest tutaj
                }
                else
                {
                    _viewModel.AddLogMessage("❌ Connection to GS succeeded but connection object is null.", LogLevel.Error);
                    _currentState = ClientConnectionState.Disconnected;
                    _viewModel.UpdateConnectionState(_currentState);
                    _cancellationTokenSource?.Cancel();
                }
            }
            else
            {
                _viewModel.AddLogMessage($"❌ Connection to Game Server {host}:{port} failed.", LogLevel.Error);
                _currentState = ClientConnectionState.Disconnected;
                _viewModel.UpdateConnectionState(_currentState);
                _cancellationTokenSource?.Cancel();
            }
        }

        public void SendLoginRequest()
        {
            if (_currentState != ClientConnectionState.ConnectedToGameServer)
            {
                _viewModel.AddLogMessage($"⚠️ Cannot send login request, not connected to Game Server or in wrong state ({_currentState}).", LogLevel.Warning);
                return;
            }
            _currentState = ClientConnectionState.Authenticating;
            _viewModel.UpdateConnectionState(_currentState);
            _viewModel.AddLogMessage("🔑 Sending Login Request...", LogLevel.Information);
            Task.Run(() => _loginService.SendLoginRequestAsync(_settings.Username, _settings.Password));
        }

        public Task SelectCharacterInteractivelyAsync(List<(string Name, CharacterClassNumber Class)> characters)
        {
            if (characters.Count == 0)
            {
                _viewModel.AddLogMessage("⚠️ No characters available on the account.", LogLevel.Warning);
                return Task.CompletedTask;
            }
            _pendingCharacterSelection = characters;
            _viewModel.DisplayCharacterList(characters);
            _viewModel.AddLogMessage("🧍 Character list received. Select a character in the UI.", LogLevel.Information);
            _currentState = ClientConnectionState.ConnectedToGameServer;
            _viewModel.UpdateConnectionState(_currentState);
            return Task.CompletedTask;
        }

        public async Task HandleSelectCharacterCommandAsync(string characterName) // Zmieniono typ argumentu
        {
            if (_currentState != ClientConnectionState.ConnectedToGameServer)
            {
                _viewModel.AddLogMessage($"Cannot select character, invalid state: {_currentState}", LogLevel.Warning);
                return;
            }

            // Znajdź klasę postaci na podstawie nazwy
            CharacterClassNumber selectedClass = CharacterClassNumber.DarkWizard;
            if (_pendingCharacterSelection != null)
            {
                var found = _pendingCharacterSelection.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase));
                if (found != default) selectedClass = found.Class;
                else
                {
                    _viewModel.AddLogMessage($"Character '{characterName}' not found in the list.", LogLevel.Warning);
                    return; // Nie kontynuuj, jeśli postać nie istnieje na liście
                }
            }
            else
            {
                _viewModel.AddLogMessage("Character list not available for validation.", LogLevel.Warning);
                // Można kontynuować bez walidacji klasy lub przerwać
                // return;
            }

            _logger.LogInformation("🎯 Handling character selection command: {Name} ({Class})", characterName, CharacterClassDatabase.GetClassName(selectedClass));

            _characterState.Name = characterName;
            _characterState.Class = selectedClass;

            _currentState = ClientConnectionState.SelectingCharacter;
            _viewModel.UpdateConnectionState(_currentState);
            _viewModel.AddLogMessage($"⏳ Selecting character: {characterName}...", LogLevel.Information);

            await _characterService.SelectCharacterAsync(characterName);
            _pendingCharacterSelection = null; // Wyczyść listę po próbie wyboru
        }

        public Task RequestServerList()
        {
            // Zmieniony warunek:
            if (_currentState != ClientConnectionState.ConnectedToConnectServer)
            {
                _viewModel.AddLogMessage($"⚠️ Cannot request server list, state is {_currentState} (Expected: ConnectedToConnectServer).", LogLevel.Warning);
                return Task.CompletedTask;
            }

            _viewModel.AddLogMessage("Requesting server list...", LogLevel.Information);
            _currentState = ClientConnectionState.RequestingServerList; // Ustaw stan PRZED wysłaniem żądania
            _viewModel.UpdateConnectionState(_currentState);
            return _connectServerService.RequestServerListAsync(); // Wyślij żądanie
        }

        /// <summary>
        /// Processes commands received from the UI (ViewModel). Made public for ViewModel access.
        /// </summary>
        public async Task ProcessCommandAsync(string commandLine)
        {
            _viewModel.AddLogMessage($"Processing UI command: {commandLine}", LogLevel.Debug);
            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var command = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();

            // General commands handled by ViewModel/UI directly
            if (command == "exit" || command == "help" || command == "clearlog")
            {
                _viewModel.AddLogMessage($"Command '{command}' handled by UI.", LogLevel.Debug);
                return;
            }

            // Route command based on current state
            if (_currentState >= ClientConnectionState.ConnectedToGameServer && _currentState != ClientConnectionState.Disconnected) // Stany od ConnectedToGameServer wzwyż
            {
                // Sprawdź, czy to nie jest komenda CS wpisana w stanie GS
                if (command == "connect" || command == "refresh")
                {
                    _viewModel.AddLogMessage($"Command '{command}' is not valid in state {_currentState}.", LogLevel.Warning);
                    return;
                }
                await HandleGameServerCommandInternalAsync(command, args);
            }
            else if (_currentState == ClientConnectionState.ReceivedServerList || _currentState == ClientConnectionState.ConnectedToConnectServer)
            {
                await HandleConnectServerCommandInternalAsync(command, args);
            }
            else
            {
                _viewModel.AddLogMessage($"Cannot process command '{command}' in state {_currentState}.", LogLevel.Warning);
            }
        }

        // Internal handlers remain private
        private async Task HandleGameServerCommandInternalAsync(string command, string[] args)
        {
            switch (command)
            {
                case "move": await HandleMoveCommandAsync(args); break;
                case "walkto": await HandleWalkToCommandAsync(args); break;
                case "walk": await HandleWalkCommandAsync(args); break;
                case "pickup": await HandlePickupCommandAsync(args); break;
                case "select": // DODAJ TEN CASE
                    if (_currentState == ClientConnectionState.ConnectedToGameServer) // Sprawdź, czy jesteśmy w stanie wyboru postaci
                    {
                        // Wywołaj metodę obsługi z argumentami (nazwą postaci)
                        // W tym przypadku argumenty pochodzą z tekstu, więc łączymy je z powrotem
                        if (args.Length >= 1)
                        {
                            string characterNameFromCommand = string.Join(" ", args); // Połącz, jeśli nazwa ma spacje
                            await HandleSelectCharacterCommandAsync(characterNameFromCommand);
                        }
                        else
                        {
                            _viewModel.AddLogMessage("Usage: select <Character Name>", LogLevel.Warning);
                        }
                    }
                    else
                    {
                        _viewModel.AddLogMessage("Cannot select character in the current state.", LogLevel.Warning);
                    }
                    break;
                default: _viewModel.AddLogMessage($"❓ Unknown game command: {command}", LogLevel.Warning); break;
            }
        }

        private async Task HandleConnectServerCommandInternalAsync(string command, string[] args)
        {
            switch (command)
            {
                case "connect":
                    if (args.Length == 1 && ushort.TryParse(args[0], out ushort serverId)) await HandleServerSelectionAsync(serverId);
                    else _viewModel.AddLogMessage("Usage: connect <ServerID>", LogLevel.Warning);
                    break;
                case "refresh": await RequestServerList(); break;
                default: _viewModel.AddLogMessage($"❓ Unknown CS command or invalid state: {command}", LogLevel.Warning); break;
            }
        }

        private async Task HandleMoveCommandAsync(string[] args)
        {
            if (!IsInGame) { _viewModel.AddLogMessage("Cannot move - not in game.", LogLevel.Warning); return; }
            if (_isWalking) { _viewModel.AddLogMessage("🚶 Cannot move - already walking.", LogLevel.Warning); return; }
            if (args.Length == 2 && byte.TryParse(args[0], out byte x) && byte.TryParse(args[1], out byte y))
            {
                await _characterService.SendInstantMoveRequestAsync(x, y);
            }
            else { _viewModel.AddLogMessage("Usage: move <X> <Y>", LogLevel.Warning); }
        }

        private async Task HandleWalkToCommandAsync(string[] args)
        {
            if (!IsInGame) { _viewModel.AddLogMessage("Cannot walk - not in game.", LogLevel.Warning); return; }
            if (_isWalking) { _viewModel.AddLogMessage("🚶 Cannot walk - already walking.", LogLevel.Warning); return; }
            if (args.Length == 2 && byte.TryParse(args[0], out byte targetX) && byte.TryParse(args[1], out byte targetY))
            {
                byte startX = _characterState.PositionX;
                byte startY = _characterState.PositionY;
                var generatedPath = GenerateSimplePathTowards(startX, startY, targetX, targetY);
                if (generatedPath.Length > 0)
                {
                    _viewModel.AddLogMessage($"🚶 Walking from ({startX},{startY}) towards ({targetX},{targetY})...", LogLevel.Information);
                    await StartWalkSequenceAsync(generatedPath, targetX, targetY);
                }
                else { _viewModel.AddLogMessage("🚶 Already at location or no path.", LogLevel.Information); }
            }
            else { _viewModel.AddLogMessage("Usage: walkto <X> <Y>", LogLevel.Warning); }
        }

        private async Task HandleWalkCommandAsync(string[] args)
        {
            if (!IsInGame) { _viewModel.AddLogMessage("Cannot walk - not in game.", LogLevel.Warning); return; }
            if (_isWalking) { _viewModel.AddLogMessage("🚶 Cannot walk - already walking.", LogLevel.Warning); return; }
            if (args.Length > 0)
            {
                var directions = args.Select(p => byte.TryParse(p, out byte dir) && dir <= 7 ? (byte?)dir : null).Where(d => d.HasValue).Select(d => d.Value).ToArray();
                if (directions.Length > 0)
                {
                    await StartWalkSequenceAsync(directions);
                }
                else { _viewModel.AddLogMessage("Invalid directions. Use numbers 0-7.", LogLevel.Warning); }
            }
            else { _viewModel.AddLogMessage("Usage: walk <dir1> [dir2] ... (0:W, 1:SW, 2:S, 3:SE, 4:E, 5:NE, 6:N, 7:NW)", LogLevel.Warning); }
        }

        private async Task HandlePickupCommandAsync(string[] args)
        {
            if (!IsInGame) { _viewModel.AddLogMessage("Cannot pickup - not in game.", LogLevel.Warning); return; }
            if (args.Length == 1)
            {
                ushort? targetItemIdRaw = null;
                string inputIdString = args[0];
                if (inputIdString.Equals("near", StringComparison.OrdinalIgnoreCase))
                {
                    targetItemIdRaw = _scopeManager.FindNearestPickupItemRawId();
                    if (!targetItemIdRaw.HasValue)
                    {
                        _viewModel.AddLogMessage("No nearby items found to pick up.", LogLevel.Information);
                        return;
                    }
                    string pickupTargetDesc = _scopeManager.TryGetScopeObjectName(targetItemIdRaw.Value, out var name) ? name : $"Item (RawID {targetItemIdRaw.Value:X4})";
                    _viewModel.AddLogMessage($"👜 Attempting to pick up nearest: {pickupTargetDesc ?? "Unknown"}...", LogLevel.Information);
                    await AttemptPickupWithRetryAsync(targetItemIdRaw.Value);
                }
                else if (ushort.TryParse(inputIdString, System.Globalization.NumberStyles.HexNumber, null, out ushort itemIdHexRaw))
                {
                    targetItemIdRaw = itemIdHexRaw;
                    _viewModel.AddLogMessage($"👜 Attempting to pick up Raw ID {targetItemIdRaw.Value:X4} (Hex)...", LogLevel.Information);
                    await SendPickupRequestAsync(targetItemIdRaw.Value);
                }
                else if (ushort.TryParse(inputIdString, out ushort itemIdDecRaw))
                {
                    targetItemIdRaw = itemIdDecRaw;
                    _viewModel.AddLogMessage($"👜 Attempting to pick up Raw ID {targetItemIdRaw.Value:X4} (Dec)...", LogLevel.Information);
                    await SendPickupRequestAsync(targetItemIdRaw.Value);
                }
                else { _viewModel.AddLogMessage($"Invalid pickup target '{inputIdString}'. Use 'near' or ID (hex/dec).", LogLevel.Warning); }
            }
            else { _viewModel.AddLogMessage("Usage: pickup <near | ItemID>", LogLevel.Warning); }
        }

        // --- Metody pomocnicze (prywatne) ---
        public void SignalMovementHandled()
        {
            if (_isWalking)
            {
                _logger.LogDebug("🚶 Movement processed, unlocking walk command.");
                _isWalking = false;
            }
        }

        public void SignalMovementHandledIfWalking()
        {
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Releasing walk lock due to error or unexpected state.");
                _isWalking = false;
            }
        }

        private async Task StartWalkSequenceAsync(byte[] path, byte? targetX = null, byte? targetY = null)
        {
            var translatedPath = path.Select(TranslateDirection).ToArray();
            if (translatedPath.Length == 0) return;
            _isWalking = true;
            try
            {
                byte firstTranslatedStep = translatedPath[0];
                byte animationNumber = 1;
                _viewModel.AddLogMessage($"🚶 Sending walk sequence ({translatedPath.Length} steps)...", LogLevel.Debug);
                await _characterService.SendAnimationRequestAsync(firstTranslatedStep, animationNumber);
                await _characterService.SendWalkRequestAsync(_characterState.PositionX, _characterState.PositionY, translatedPath);
            }
            catch (Exception ex)
            {
                _viewModel.AddLogMessage($"Error during walk sequence: {ex.Message}", LogLevel.Error);
                _isWalking = false;
            }
        }

        private byte TranslateDirection(byte standardDirection)
        {
            if (_serverDirectionMap.TryGetValue(standardDirection, out byte serverDirection))
            {
                return serverDirection;
            }
            _logger.LogWarning("No translation found for direction {StandardDir}, using original.", standardDirection);
            return standardDirection;
        }

        private byte[] GenerateSimplePathTowards(byte startX, byte startY, byte targetX, byte targetY)
        {
            const int MaxStepsPerPacket = 15;
            var path = new List<byte>(MaxStepsPerPacket);
            int currentX = startX; int currentY = startY;

            for (int i = 0; i < MaxStepsPerPacket; i++)
            {
                int dx = targetX - currentX; int dy = targetY - currentY;
                if (dx == 0 && dy == 0) break;
                byte bestDirection = 0xFF;

                if (dx == 0) bestDirection = dy > 0 ? (byte)2 : (byte)6; // S lub N
                else if (dy == 0) bestDirection = dx > 0 ? (byte)4 : (byte)0; // E lub W
                else if (dx > 0 && dy > 0) bestDirection = 3; // SE
                else if (dx < 0 && dy > 0) bestDirection = 1; // SW
                else if (dx > 0 && dy < 0) bestDirection = 5; // NE
                else if (dx < 0 && dy < 0) bestDirection = 7; // NW

                if (bestDirection <= 7)
                {
                    path.Add(bestDirection);
                    switch (bestDirection) { case 0: currentX--; break; case 1: currentX--; currentY++; break; case 2: currentY++; break; case 3: currentX++; currentY++; break; case 4: currentX++; break; case 5: currentX++; currentY--; break; case 6: currentY--; break; case 7: currentX--; currentY--; break; }
                }
                else { _viewModel.AddLogMessage($"Pathfinding failed from ({currentX},{currentY}) to ({targetX},{targetY})", LogLevel.Warning); break; }
            }
            return path.ToArray();
        }

        private async Task AttemptPickupWithRetryAsync(ushort targetItemIdRaw)
        {
            int attempts = 0; const int maxAttempts = 3; bool pickupSuccess = false;
            DateTime startTime = DateTime.UtcNow; const int retryDelayMs = 200; const int timeoutSeconds = 5;

            while (!pickupSuccess && attempts < maxAttempts && (DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
            {
                PickupHandled = false; LastPickupSucceeded = false;
                await SendPickupRequestAsync(targetItemIdRaw);
                await Task.Delay(retryDelayMs);

                if (PickupHandled) { pickupSuccess = LastPickupSucceeded; break; }
                else { _viewModel.AddLogMessage($"Pickup attempt {attempts + 1}: No response yet...", LogLevel.Debug); }

                attempts++;
            }

            if (!PickupHandled) { _viewModel.AddLogMessage("Pickup attempt timed out or failed without response.", LogLevel.Warning); }
            else if (pickupSuccess) { _viewModel.AddLogMessage("✅ Pickup successful confirmed.", LogLevel.Information); }
            else { _viewModel.AddLogMessage("❌ Pickup failed or item stacked.", LogLevel.Warning); }
        }

        private async Task SendPickupRequestAsync(ushort targetItemIdRaw)
        {
            ushort itemIdMasked = (ushort)(targetItemIdRaw & 0x7FFF);
            try
            {
                if (_packetRouter.TargetVersion == TargetProtocolVersion.Version075)
                {
                    await _connectionManager.Connection.SendPickupItemRequest075Async(itemIdMasked);
                }
                else
                {
                    await _connectionManager.Connection.SendPickupItemRequestAsync(itemIdMasked);
                }
                _viewModel.AddLogMessage($"✔️ Pickup request sent for RAW ID {targetItemIdRaw:X4}.", LogLevel.Information);
            }
            catch (Exception ex)
            {
                _viewModel.AddLogMessage($"💥 Error sending pickup packet for Masked ID {itemIdMasked:X4}: {ex.Message}", LogLevel.Error);
            }
        }

        public void SetInGameStatus(bool inGame)
        {
            bool changed = _characterState.IsInGame != inGame;
            _characterState.IsInGame = inGame;

            if (changed)
            {
                if (inGame)
                {
                    _currentState = ClientConnectionState.InGame;
                    _viewModel.AddLogMessage("🟢 Character is now in-game. Enter commands in the input box.", LogLevel.Information);
                }
                else
                {
                    if (_connectionManager.IsConnected)
                    {
                        _currentState = ClientConnectionState.ConnectedToGameServer;
                        _viewModel.AddLogMessage("🚪 Character left the game world (still connected). Select a character.", LogLevel.Information);
                    }
                    else
                    {
                        _currentState = ClientConnectionState.Disconnected;
                        _viewModel.AddLogMessage("🚪 Character left the game world (disconnected).", LogLevel.Information);
                    }
                }
                _viewModel.UpdateConnectionState(_currentState);
                _viewModel.UpdateCharacterStateDisplay();
            }
        }

        private ValueTask HandlePacketAsync(ReadOnlySequence<byte> sequence)
        {
            return new ValueTask(_packetRouter.RoutePacketAsync(sequence));
        }

        private ValueTask HandleDisconnectAsync()
        {
            _currentState = ClientConnectionState.Disconnected;
            _viewModel.UpdateConnectionState(_currentState);
            _viewModel.AddLogMessage("🔌 Connection lost.", LogLevel.Warning);
            SetInGameStatus(false);
            return new ValueTask(_packetRouter.OnDisconnected());
        }

        public async ValueTask DisposeAsync()
        {
            _viewModel.AddLogMessage("🧹 Disposing client resources...", LogLevel.Information);
            _cancellationTokenSource?.Cancel();
            await _connectionManager.DisposeAsync();
            _cancellationTokenSource?.Dispose();
            _viewModel.AddLogMessage("🛑 Client stopped.", LogLevel.Information);
            GC.SuppressFinalize(this);
        }

        internal void UpdateConsoleTitle()
        {
            return;
        }
    }
}