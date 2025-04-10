using System.Buffers; // Required for ReadOnlySequence<byte>
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;

namespace MuOnlineConsole
{
    /// <summary>
    /// Enum representing protocol versions for compatibility handling.
    /// </summary>
    public enum TargetProtocolVersion
    {
        Season6,
        Version097,
        Version075
    }

    /// <summary>
    /// Represents the connection state of the client.
    /// </summary>
    public enum ClientConnectionState
    {
        Initial,
        ConnectingToConnectServer,
        ConnectedToConnectServer,
        RequestingServerList,
        ReceivedServerList,
        SelectingServer,
        RequestingConnectionInfo,
        ReceivedConnectionInfo,
        ConnectingToGameServer,
        ConnectedToGameServer, // Ready to send login
        Authenticating,
        SelectingCharacter,
        InGame,
        Disconnected
    }

    /// <summary>
    /// Main client class managing connection, login, character handling and packet routing.
    /// </summary>
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

        private ClientConnectionState _currentState = ClientConnectionState.Initial;
        private List<ServerInfo> _serverList = new();
        private List<string>? _pendingCharacterSelection = null;

        // Character State (Game Server related)
        private uint _currentHealth = 0;
        private uint _maximumHealth = 1;
        private uint _currentShield = 0;
        private uint _maximumShield = 0;
        private uint _currentMana = 0;
        private uint _maximumMana = 1;
        private uint _currentAbility = 0;
        private uint _maximumAbility = 0;
        private ushort _strength = 0;
        private ushort _agility = 0;
        private ushort _vitality = 0;
        private ushort _energy = 0;
        private ushort _leadership = 0;
        private string _characterName = "???";
        private bool _isInGame = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private ushort _characterId = 0xFFFF;
        private byte _currentX = 0;
        private byte _currentY = 0;
        private bool _isWalking = false;
        private readonly string _username;
        private readonly string _password;
        private Dictionary<byte, byte> _serverDirectionMap = new();
        private readonly ConcurrentDictionary<ushort, ScopeObject> _objectsInScope = new();


        public bool IsInGame => _isInGame;
        public bool IsConnected => _connectionManager.IsConnected;
        public ushort GetCharacterId() => _characterId;
        public string GetCharacterName() => _characterName;
        public bool LastPickupSucceeded { get; set; } = false;
        public bool PickupHandled { get; set; } = false;

        public SimpleLoginClient(ILoggerFactory loggerFactory, MuOnlineSettings settings)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SimpleLoginClient>();
            _settings = settings; // Store settings

            var clientVersionBytes = Encoding.ASCII.GetBytes(settings.ClientVersion);
            var clientSerialBytes = Encoding.ASCII.GetBytes(settings.ClientSerial);
            var targetVersion = Enum.Parse<TargetProtocolVersion>(settings.ProtocolVersion, ignoreCase: true);

            _connectionManager = new ConnectionManager(loggerFactory, EncryptKeys, DecryptKeys);
            _loginService = new LoginService(_connectionManager, _logger, clientVersionBytes, clientSerialBytes, Xor3Keys);
            _characterService = new CharacterService(_connectionManager, _logger);
            _connectServerService = new ConnectServerService(_connectionManager, _loggerFactory.CreateLogger<ConnectServerService>());
            _packetRouter = new PacketRouter(loggerFactory.CreateLogger<PacketRouter>(), _characterService, _loginService, targetVersion, this, _settings);
            _serverDirectionMap = settings.DirectionMap;
            _username = settings.Username;
            _password = settings.Password;
        }

        /// <summary>
        /// Main loop: connect and run client logic.
        /// </summary>
        public async Task RunAsync()
        {
            _logger.LogInformation("🚀 Starting client execution (Target: {Version})...", _packetRouter.TargetVersion);
            _logger.LogInformation("🔍 Using username '{Username}' and password '{Password}'", _username, _password);
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            // Start Command Loop in background
            var commandLoopTask = Task.Run(() => CommandLoopAsync(cancellationToken), cancellationToken);

            // Start Connection Process
            await ConnectToConnectServerAsync(cancellationToken);

            // Keep the application running until cancelled or command loop finishes
            try
            {
                // Wait for either cancellation or the command loop to naturally end (e.g., 'exit' command)
                await commandLoopTask; // If command loop ends, we should shut down.
                if (!cancellationToken.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel(); // Ensure shutdown if command loop exited normally
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🛑 Main execution loop cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Unexpected error in main execution loop.");
            }
            finally
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel(); // Ensure cancellation token is set for cleanup
                }
            }

            _logger.LogInformation("Shutting down client...");
        }

        private async Task ConnectToConnectServerAsync(CancellationToken cancellationToken)
        {
            _currentState = ClientConnectionState.ConnectingToConnectServer;
            _packetRouter.SetRoutingMode(true); // Route Connect Server packets

            if (await _connectionManager.ConnectAsync(_settings.ConnectServerHost, _settings.ConnectServerPort, false, cancellationToken)) // Connect Server usually doesn't use encryption
            {
                _currentState = ClientConnectionState.ConnectedToConnectServer;
                _connectionManager.Connection.PacketReceived += HandlePacketAsync;
                _connectionManager.Connection.Disconnected += HandleDisconnectAsync;

                _logger.LogInformation("✅ Successfully connected to Connect Server. Requesting server list...");
                _currentState = ClientConnectionState.RequestingServerList;
                await _connectServerService.RequestServerListAsync();
            }
            else
            {
                _logger.LogError("❌ Connection to Connect Server failed.");
                _currentState = ClientConnectionState.Disconnected;
                _cancellationTokenSource?.Cancel(); // Stop the client if CS connection fails
            }
        }

        /// <summary>
        /// Stores the received server list and prompts the user.
        /// </summary>
        public void StoreServerList(List<ServerInfo> servers)
        {
            _serverList = servers;
            _currentState = ClientConnectionState.ReceivedServerList;
            _logger.LogInformation("📝 Server list received with {Count} servers.", servers.Count);
            Console.WriteLine("=== Available Servers ===");
            if (servers.Count == 0)
            {
                Console.WriteLine("No servers available.");
            }
            else
            {
                for (int i = 0; i < servers.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. ID: {servers[i].ServerId}, Load: {servers[i].LoadPercentage}%");
                }
            }
            Console.WriteLine("👉 Type 'connect <number>' (e.g., 'connect 1') to connect to a server.");
            Console.WriteLine("👉 Or type 'refresh' to request the server list again.");
        }

        /// <summary>
        /// Handles the user selecting a server from the list.
        /// </summary>
        private async Task HandleServerSelection(ushort serverId)
        {
            if (_currentState != ClientConnectionState.ReceivedServerList)
            {
                _logger.LogWarning("⚠️ Cannot select server, not in the correct state ({State}).", _currentState);
                return;
            }

            _logger.LogInformation("👉 Requesting connection details for Server ID {ServerId}...", serverId);
            _currentState = ClientConnectionState.RequestingConnectionInfo;
            await _connectServerService.RequestConnectionInfoAsync(serverId);
        }

        /// <summary>
        /// Disconnects from the Connect Server and connects to the specified Game Server.
        /// </summary>
        public async void SwitchToGameServer(string host, int port)
        {
            if (_currentState != ClientConnectionState.RequestingConnectionInfo)
            {
                _logger.LogWarning("⚠️ Received game server info in unexpected state ({State}). Ignoring.", _currentState);
                return;
            }
            _currentState = ClientConnectionState.ReceivedConnectionInfo;

            _logger.LogInformation("🔌 Disconnecting from Connect Server...");

            // Get a reference to the current connection BEFORE disconnecting
            var oldConnection = _connectionManager.Connection;

            // Unsubscribe event handlers from the OLD connection immediately
            if (oldConnection != null)
            {
                oldConnection.PacketReceived -= HandlePacketAsync;
                oldConnection.Disconnected -= HandleDisconnectAsync;
                _logger.LogDebug("Unsubscribed event handlers from old Connect Server connection.");
            }

            await _connectionManager.DisconnectAsync(); // Now disconnect and dispose

            _logger.LogInformation("🔌 Connecting to Game Server {Host}:{Port}...", host, port);
            _currentState = ClientConnectionState.ConnectingToGameServer;
            _packetRouter.SetRoutingMode(false);

            if (await _connectionManager.ConnectAsync(host, port, true, _cancellationTokenSource?.Token ?? default))
            {
                _currentState = ClientConnectionState.ConnectedToGameServer;

                // Subscribe event handlers to the NEW connection
                if (_connectionManager.Connection != null)
                {
                    _connectionManager.Connection.PacketReceived += HandlePacketAsync;
                    _connectionManager.Connection.Disconnected += HandleDisconnectAsync;
                    _logger.LogDebug("Subscribed event handlers to new Game Server connection.");
                }
                else
                {
                    // This case should ideally not happen if ConnectAsync returned true, but good to have a check.
                    _logger.LogError("❌ Failed to get new connection object after connecting to Game Server.");
                    _currentState = ClientConnectionState.Disconnected;
                    _cancellationTokenSource?.Cancel(); // Stop the client
                    return; // Exit the method
                }

                _logger.LogInformation("✅ Successfully connected to Game Server. Ready to login.");
                // F1 00 handler will trigger login
            }
            else
            {
                _logger.LogError("❌ Connection to Game Server {Host}:{Port} failed.", host, port);
                _currentState = ClientConnectionState.Disconnected;
                _cancellationTokenSource?.Cancel();
            }
        }

        /// <summary>
        /// Sends the login request to the Game Server.
        /// Should be called after successfully connecting to the GS.
        /// </summary>
        public void SendLoginRequest()
        {
            if (_currentState != ClientConnectionState.ConnectedToGameServer)
            {
                _logger.LogWarning("⚠️ Cannot send login request, not connected to Game Server or in wrong state ({State}).", _currentState);
                return;
            }
            _currentState = ClientConnectionState.Authenticating;
            Task.Run(() => _loginService.SendLoginRequestAsync(_username, _password));
        }


        private ValueTask HandlePacketAsync(ReadOnlySequence<byte> sequence)
        {
            // Route based on the current mode set in PacketRouter
            return new ValueTask(_packetRouter.RoutePacketAsync(sequence));
        }

        private ValueTask HandleDisconnectAsync()
        {
            // Let the PacketRouter handle the logic based on its current mode
            return new ValueTask(_packetRouter.OnDisconnected());
        }

        private static double DistanceSquared(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return (dx * dx) + (dy * dy);
        }

        /// <summary>
        /// Finds the nearest item (Item or Money) within pickup range and returns its Raw ID.
        /// Ignores objects at position (0,0).
        /// </summary>
        /// <returns>The Raw ID of the nearest item, or null if nothing is found within range.</returns>
        private ushort? FindNearestPickupItemRawId()
        {
            double minDistanceSq = double.MaxValue;
            ScopeObject? nearestObject = null;

            // Combine the list of items and money to check
            var groundItems = GetScopeItems(ScopeObjectType.Item).Concat(GetScopeItems(ScopeObjectType.Money));

            foreach (var obj in groundItems)
            {
                // Ignore objects at (0,0), they might be invalid
                if (obj.PositionX == 0 && obj.PositionY == 0) continue;

                double distSq = DistanceSquared(_currentX, _currentY, obj.PositionX, obj.PositionY);
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    nearestObject = obj;
                }
            }

            // Optional check for maximum pickup distance
            const double maxPickupDistanceSq = 5 * 5; // Example: Maximum distance of 5 units
            if (nearestObject != null && minDistanceSq <= maxPickupDistanceSq)
            {
                _logger.LogInformation("Nearest object found: {Object} at distance squared {DistanceSq}", nearestObject, minDistanceSq);
                // Return the RawId of the found object
                return nearestObject.RawId;
            }
            else if (nearestObject != null)
            {
                _logger.LogInformation("Nearest object {Object} is too far away (Distance Squared: {DistanceSq})", nearestObject, minDistanceSq);
                return null;
            }
            else
            {
                _logger.LogInformation("No items or money found nearby.");
                return null;
            }
        }

        /// <summary>
        /// Tries to get the name or description of an object currently in scope by its ID.
        /// Handles player names, NPC names/types, and descriptions for items/money.
        /// Automatically applies the ID mask (removes the highest bit).
        /// </summary>
        /// <param name="id">The potentially masked ID received from the server.</param>
        /// <param name="name">When this method returns, contains the name or description of the object, if found; otherwise, null.</param>
        /// <returns>True if an object with the masked ID was found in scope; otherwise, false.</returns>
        public bool TryGetScopeObjectName(ushort id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string? name)
        {
            // Always mask the ID before looking it up, as dictionary keys are masked IDs.
            ushort maskedId = (ushort)(id & 0x7FFF);

            if (_objectsInScope.TryGetValue(maskedId, out ScopeObject? scopeObject))
            {
                switch (scopeObject.ObjectType)
                {
                    case ScopeObjectType.Player:
                        name = ((PlayerScopeObject)scopeObject).Name;
                        return true;
                    case ScopeObjectType.Npc:
                    case ScopeObjectType.Monster: // Treat Monster similar to NPC for naming
                        var npcObject = (NpcScopeObject)scopeObject;
                        // Prefer name if available, otherwise use type number
                        name = !string.IsNullOrWhiteSpace(npcObject.Name) ? npcObject.Name : $"NPC Type {npcObject.TypeNumber}";
                        return true;
                    case ScopeObjectType.Item:
                        name = ((ItemScopeObject)scopeObject).ItemDescription; // Use the generated description
                        return true;
                    case ScopeObjectType.Money:
                        name = "Zen"; // Money is typically just "Zen"
                        return true;
                    default:
                        name = $"Unknown Object Type ({scopeObject.ObjectType})";
                        _logger.LogWarning("Found object with ID {MaskedId:X4} but unknown type {Type}", maskedId, scopeObject.ObjectType);
                        return true; // Technically found, but type is unknown
                }
            }
            else
            {
                // Object not found in scope
                name = null;
                _logger.LogTrace("Object with masked ID {MaskedId:X4} (from RawID {RawId:X4}) not found in scope.", maskedId, id);
                return false;
            }
        }

        private async Task CommandLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⌨️ Command loop started. Type 'exit' to quit.");
            while (!cancellationToken.IsCancellationRequested) // Keep loop running even if disconnected, allows reconnect attempts etc.
            {
                string? commandLine = null;
                try
                {
                    // Use ReadLineAsync with cancellation token 
                    commandLine = await Console.In.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; } // Expected when shutting down
                catch (IOException ex) { _logger.LogError(ex, "Error reading command line input (IO)."); break; }
                catch (Exception ex) { _logger.LogError(ex, "Error reading command line input."); break; }

                if (cancellationToken.IsCancellationRequested || commandLine == null) break;

                var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var command = parts[0].ToLowerInvariant();

                // --- General Commands ---
                if (command == "exit")
                {
                    _logger.LogInformation("Received 'exit' command. Shutting down...");
                    _cancellationTokenSource?.Cancel();
                    return; // Exit the loop
                }

                // --- Connect Server State Commands ---
                if (_currentState < ClientConnectionState.ConnectingToGameServer)
                {
                    switch (command)
                    {
                        case "servers":
                        case "list":
                            if (_serverList.Count > 0)
                            {
                                StoreServerList(_serverList); // Re-display the list
                            }
                            else
                            {
                                _logger.LogWarning("No server list available. Use 'refresh'.");
                            }
                            break;

                        case "refresh":
                            if (_currentState == ClientConnectionState.ConnectedToConnectServer || _currentState == ClientConnectionState.ReceivedServerList)
                            {
                                _logger.LogInformation("Requesting updated server list...");
                                _currentState = ClientConnectionState.RequestingServerList;
                                await _connectServerService.RequestServerListAsync();
                            }
                            else
                            {
                                _logger.LogWarning("Cannot refresh server list, not connected to Connect Server.");
                            }
                            break;

                        case "connect":
                            if (_currentState != ClientConnectionState.ReceivedServerList)
                            {
                                _logger.LogWarning("Please wait for the server list or use 'refresh'.");
                                break;
                            }
                            if (parts.Length != 2 || !int.TryParse(parts[1], out int index) || index < 1 || index > _serverList.Count)
                            {
                                _logger.LogWarning("Usage: connect <number> (1-{Max})", _serverList.Count);
                                break;
                            }
                            var selectedServer = _serverList[index - 1];
                            await HandleServerSelection(selectedServer.ServerId);
                            break;

                        default:
                            _logger.LogWarning("❓ Unknown command or command not valid in current state ({State}): {Command}", _currentState, command);
                            break;
                    }
                }
                // --- Game Server State Commands ---
                else if (_currentState >= ClientConnectionState.ConnectedToGameServer)
                {
                    switch (command)
                    {
                        case "select": // Character Selection
                            if (_pendingCharacterSelection == null)
                            {
                                _logger.LogWarning("No character list available to select from.");
                                break;
                            }
                            if (parts.Length != 2 || !int.TryParse(parts[1], out int index) || index < 1 || index > _pendingCharacterSelection.Count)
                            {
                                _logger.LogWarning("Usage: select <number> (1-{Max})", _pendingCharacterSelection.Count);
                                break;
                            }
                            var selectedName = _pendingCharacterSelection[index - 1];
                            _logger.LogInformation("🎯 Selected character: {Name}", selectedName);
                            _characterName = selectedName; // Store the name
                            _currentState = ClientConnectionState.SelectingCharacter;
                            _ = _characterService.SelectCharacterAsync(selectedName);
                            _pendingCharacterSelection = null; // Clear pending list
                            break;

                        case "nearby":
                        case "scope":
                            ListObjectsInScope();
                            break;

                        case "move":
                            if (!IsInGame) { _logger.LogWarning("Cannot move - character is not in game."); continue; }
                            if (_isWalking) { _logger.LogWarning("🚶 Character is currently walking, cannot use 'move'."); continue; }
                            if (parts.Length == 3 && byte.TryParse(parts[1], out byte x) && byte.TryParse(parts[2], out byte y))
                            {
                                await _characterService.SendInstantMoveRequestAsync(x, y);
                            }
                            else { _logger.LogWarning("Invalid 'move' command format. Use: move X Y"); }
                            break;

                        case "walkto":
                            if (!IsInGame) { _logger.LogWarning("Cannot walk - character is not in game."); continue; }
                            if (_isWalking) { _logger.LogWarning("🚶 Character is already moving, please wait."); continue; }
                            if (parts.Length == 3 && byte.TryParse(parts[1], out byte targetX) && byte.TryParse(parts[2], out byte targetY))
                            {
                                byte startX = _currentX;
                                byte startY = _currentY;
                                var generatedPath = GenerateSimplePathTowards(startX, startY, targetX, targetY);

                                if (generatedPath.Length > 0)
                                {
                                    var translatedPath = generatedPath.Select(TranslateDirection).ToArray();
                                    _isWalking = true;
                                    try
                                    {
                                        byte firstTranslatedStep = translatedPath[0];
                                        byte animationNumber = 0; // Default walk animation?
                                        _logger.LogDebug("🚶 Sending AnimationRequest before walk. Translated Direction: {Dir}, Anim: {Anim}", firstTranslatedStep, animationNumber);
                                        await _characterService.SendAnimationRequestAsync(firstTranslatedStep, animationNumber);

                                        _logger.LogDebug("🚶 Sending WalkRequest. Client Position: ({CurrentX},{CurrentY}), Target: ({TargetX},{TargetY}), Original Path: [{OrigPath}], Translated Path: [{TransPath}]",
                                                            _currentX, _currentY, targetX, targetY, string.Join(",", generatedPath), string.Join(",", translatedPath));
                                        await _characterService.SendWalkRequestAsync(_currentX, _currentY, translatedPath);
                                    }
                                    catch (OperationCanceledException) { _isWalking = false; break; }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Error during walkto sequence.");
                                        _isWalking = false;
                                    }
                                }
                                else { _logger.LogInformation("🚶 Already at target location or no path."); }
                            }
                            else { _logger.LogWarning("Invalid 'walkto' format. Use: walkto X Y"); }
                            break;

                        case "walk":
                            if (!IsInGame) { _logger.LogWarning("Cannot walk - character is not in game."); continue; }
                            if (_isWalking) { _logger.LogWarning("🚶 Character is already moving, please wait."); continue; }
                            if (parts.Length > 1)
                            {
                                var directions = parts.Skip(1)
                                                    .Select(p => byte.TryParse(p, out byte dir) && dir <= 7 ? (byte?)dir : null)
                                                    .Where(d => d.HasValue)
                                                    .Select(d => d.Value)
                                                    .ToArray();

                                if (directions.Length > 0)
                                {
                                    var translatedDirections = directions.Select(TranslateDirection).ToArray();
                                    _isWalking = true;
                                    try
                                    {
                                        byte firstTranslatedStep = translatedDirections[0];
                                        byte animationNumber = 0; // Default walk animation?
                                        _logger.LogDebug("🚶 Sending AnimationRequest before walk. Translated Direction: {Dir}, Anim: {Anim}", firstTranslatedStep, animationNumber);
                                        await _characterService.SendAnimationRequestAsync(firstTranslatedStep, animationNumber);

                                        _logger.LogInformation("🚶 Sending WalkRequest packet with start ({StartX},{StartY}), {Steps} steps (translated)...", _currentX, _currentY, translatedDirections.Length);
                                        await _characterService.SendWalkRequestAsync(_currentX, _currentY, translatedDirections);
                                    }
                                    catch (OperationCanceledException) { _isWalking = false; break; }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Error during walk sequence.");
                                        _isWalking = false;
                                    }
                                }
                                else { _logger.LogWarning("Invalid 'walk' arguments. Use numeric directions 0-7."); }
                            }
                            else
                            {
                                _logger.LogWarning("Invalid 'walk' command format. Use: walk <dir1> [dir2] ...");
                                _logger.LogInformation("Directions: 0:W, 1:SW, 2:S, 3:SE, 4:E, 5:NE, 6:N, 7:NW");
                            }
                            break;

                        case "pickup":
                            if (!IsInGame)
                            {
                                _logger.LogWarning("Cannot pick up item - character is not in game.");
                                continue;
                            }

                            if (parts.Length == 2)
                            {
                                ushort? targetItemIdRaw = null;
                                string inputIdString = parts[1];

                                if (inputIdString.ToLowerInvariant() == "near")
                                {
                                    targetItemIdRaw = FindNearestPickupItemRawId();
                                    if (!targetItemIdRaw.HasValue)
                                    {
                                        _logger.LogInformation("No nearby items found to pick up.");
                                        continue;
                                    }

                                    _logger.LogInformation("👜 Attempting to pick up nearest item (Raw ID {RawId:X4})...", targetItemIdRaw.Value);

                                    // Retry loop: up to 20 attempts, sometimes an item is blocked because it belongs to another player u have to wait a certain amount of time
                                    int attempts = 0;
                                    const int maxAttempts = 20;
                                    bool pickupSuccess = false;
                                    DateTime startTime = DateTime.UtcNow;

                                    while (!pickupSuccess && attempts < maxAttempts && (DateTime.UtcNow - startTime).TotalSeconds < 20)
                                    {
                                        await SendPickupRequest(targetItemIdRaw.Value);
                                        await Task.Delay(1000); // Wait 1000 ms between retries

                                        // Check if the item is still available. If not, we assume it was picked up.
                                        ushort? currentItemId = FindNearestPickupItemRawId();
                                        if (!currentItemId.HasValue || currentItemId.Value != targetItemIdRaw.Value)
                                        {
                                            pickupSuccess = true;
                                            _logger.LogInformation("✅ Item (Raw ID {RawId:X4}) was successfully picked up after {Attempts} attempts.", targetItemIdRaw.Value, attempts + 1);
                                        }

                                        attempts++;
                                    }

                                    if (!pickupSuccess)
                                    {
                                        _logger.LogWarning("⚠️ Failed to pick up item (Raw ID {RawId:X4}) after {Attempts} attempts.", targetItemIdRaw.Value, attempts);
                                    }
                                }
                                else if (ushort.TryParse(inputIdString, System.Globalization.NumberStyles.HexNumber, null, out ushort itemIdHexRaw))
                                {
                                    targetItemIdRaw = itemIdHexRaw;
                                    _logger.LogInformation("👜 Attempting to pick up item with Raw ID {ItemId:X4} (parsed as hex from input '{Input}')...", targetItemIdRaw.Value, inputIdString);
                                    await SendPickupRequest(targetItemIdRaw.Value);
                                }
                                else if (ushort.TryParse(inputIdString, out ushort itemIdDecRaw))
                                {
                                    targetItemIdRaw = itemIdDecRaw;
                                    _logger.LogInformation("👜 Attempting to pick up item with Raw ID {ItemId:X4} (parsed as decimal from input '{Input}')...", targetItemIdRaw.Value, inputIdString);
                                    await SendPickupRequest(targetItemIdRaw.Value);
                                }
                                else
                                {
                                    _logger.LogWarning("Invalid pickup target '{Target}'. Use: 'pickup near' or 'pickup <ItemID_Hex_Or_Dec>'.", inputIdString);
                                    continue;
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Invalid 'pickup' command format. Use: 'pickup near' or 'pickup <ItemID>'.");
                            }
                            break;

                        default:
                            _logger.LogWarning("❓ Unknown command or command not valid in current state ({State}): {Command}", _currentState, command);
                            break;
                    }
                }
                else // Disconnected or Initial state
                {
                    _logger.LogWarning("❓ Not connected. Available commands: 'exit'.");
                }
            }
            _logger.LogInformation("⌨️ Command loop ended.");
        }

        private async Task SendPickupRequest(ushort targetItemIdRaw)
        {
            ushort itemIdMasked = (ushort)(targetItemIdRaw & 0x7FFF);

            try
            {
                using var owner = MemoryPool<byte>.Shared.Rent(32);
                var writer = new ArrayBufferWriter<byte>();

                // Build the pickup packet using the raw item ID (it will be masked before sending)
                int packetSize = PacketBuilder.BuildPickupItemRequestPacket(writer, targetItemIdRaw, _packetRouter.TargetVersion);
                var packetBytes = writer.WrittenSpan;

                _logger.LogDebug("   -> Preparing pickup packet (Masked ID: {MaskedId:X4}, Version: {Version}): {PacketData}",
                                 itemIdMasked, _packetRouter.TargetVersion, Convert.ToHexString(packetBytes));

                // Send the pickup request packet using C3 header
                await _connectionManager.Connection.SendPickupItemRequestAsync(itemIdMasked);
                _logger.LogInformation("✔️ Pickup request sent for RAW ID {RawID:X4}.", targetItemIdRaw);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while building or sending pickup packet for Masked ID {MaskedId:X4}.", itemIdMasked);
            }
        }

        private byte TranslateDirection(byte standardDirection)
        {
            if (_serverDirectionMap.TryGetValue(standardDirection, out byte serverDirection))
            {
                _logger.LogTrace("Translating direction {StandardDir} -> {ServerDir}", standardDirection, serverDirection);
                return serverDirection;
            }
            _logger.LogWarning("Translation not found for direction {StandardDir}, using original.", standardDirection);
            return standardDirection;
        }

        public Task SelectCharacterInteractivelyAsync(List<string> characterNames)
        {
            if (characterNames.Count == 0)
            {
                _logger.LogWarning("⚠️ No characters available on the account.");
                return Task.CompletedTask;
            }

            Console.WriteLine("🧍 Available characters:");
            for (int i = 0; i < characterNames.Count; i++)
            {
                Console.WriteLine($"  {i + 1}. {characterNames[i]}");
            }

            Console.WriteLine("👉 Type: select <number>  (e.g. 'select 1') to choose a character.");

            _pendingCharacterSelection = characterNames;

            return Task.CompletedTask;
        }

        public void SignalMovementHandled()
        {
            if (_isWalking)
            {
                _logger.LogDebug("🚶 Movement processed (or failed), unlocking walk command.");
                _isWalking = false;
            }
        }

        // Generates a simple path TOWARDS the target (without pathfinding!)
        private byte[] GenerateSimplePathTowards(byte startX, byte startY, byte targetX, byte targetY)
        {
            const int MaxStepsPerPacket = 15;
            var path = new List<byte>(MaxStepsPerPacket);
            int currentX = startX;
            int currentY = startY;

            for (int i = 0; i < MaxStepsPerPacket; i++)
            {
                int dx = targetX - currentX;
                int dy = targetY - currentY;

                if (dx == 0 && dy == 0)
                {
                    break; // Reached target
                }

                byte bestDirection = 0xFF; // Invalid direction

                // Determine the best direction based on the largest difference
                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    if (dx > 0) bestDirection = 4; // East
                    else bestDirection = 0; // West
                }
                else if (Math.Abs(dy) > Math.Abs(dx))
                {
                    if (dy > 0) bestDirection = 2; // South
                    else bestDirection = 6; // North
                }
                else // Diagonal movement if dx and dy are equal non-zero
                {
                    if (dx > 0 && dy > 0) bestDirection = 3; // SE
                    else if (dx < 0 && dy > 0) bestDirection = 1; // SW
                    else if (dx > 0 && dy < 0) bestDirection = 5; // NE
                    else if (dx < 0 && dy < 0) bestDirection = 7; // NW
                }


                if (bestDirection <= 7)
                {
                    path.Add(bestDirection);

                    // Update current position based on the chosen direction
                    switch (bestDirection)
                    {
                        case 0: currentX--; break;
                        case 1: currentX--; currentY++; break;
                        case 2: currentY++; break;
                        case 3: currentX++; currentY++; break;
                        case 4: currentX++; break;
                        case 5: currentX++; currentY--; break;
                        case 6: currentY--; break;
                        case 7: currentX--; currentY--; break;
                    }
                }
                else
                {
                    _logger.LogWarning("Could not determine direction from ({curX},{curY}) to ({tarX},{tarY})", currentX, currentY, targetX, targetY);
                    break; // Should not happen if dx or dy is non-zero
                }
            }

            return path.ToArray();
        }

        /// <summary>
        /// Requests the server list from the Connect Server.
        /// </summary>
        public Task RequestServerList()
        {
            if (_currentState != ClientConnectionState.ConnectedToConnectServer && _currentState != ClientConnectionState.ReceivedServerList)
            {
                _logger.LogWarning("⚠️ Cannot request server list, not connected to Connect Server or in wrong state ({State}).", _currentState);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Requesting server list...");
            _currentState = ClientConnectionState.RequestingServerList;
            return _connectServerService.RequestServerListAsync();
        }

        public void SetInGameStatus(bool inGame)
        {
            bool changed = _isInGame != inGame;
            _isInGame = inGame;
            if (changed)
            {
                if (inGame)
                {
                    _currentState = ClientConnectionState.InGame;
                    _logger.LogInformation("🟢 Character is now in-game. You can enter commands (e.g., 'move X Y').");
                }
                else
                {
                    // If we were in game and now we are not, it means we left the game world.
                    // Reset state appropriately, maybe back to character selection or disconnected.
                    // For now, let's assume disconnected if not explicitly selecting character again.
                    _currentState = ClientConnectionState.ConnectedToGameServer; // Or Disconnected? Needs refinement based on logout packet handling
                    _logger.LogInformation("🚪 Character has left the game world.");
                }
            }
        }

        public void SetCharacterId(ushort id)
        {
            _characterId = id;
            _logger.LogInformation("🆔 Character ID set: {CharacterId:X4}", _characterId);
        }

        public void SetPosition(byte x, byte y)
        {
            _logger.LogDebug("🔄 Updating position: Old ({OldX},{OldY}), New ({NewX},{NewY})", _currentX, _currentY, x, y);
            _currentX = x;
            _currentY = y;
            _logger.LogInformation("📍 Position set: ({X}, {Y})", _currentX, _currentY);
            UpdateConsoleTitle();

            if (_isWalking)
            {
                _logger.LogDebug("🚶 Movement processed (or failed), unlocking walk command.");
                _isWalking = false;
            }
        }


        public void SetCharacterName(string name)
        {
            _characterName = name;
            UpdateConsoleTitle();
        }

        public void UpdateCurrentHealthShield(uint currentHealth, uint currentShield)
        {
            _currentHealth = currentHealth;
            _currentShield = currentShield;
            _logger.LogInformation("❤️ HP: {CurrentHealth}/{MaximumHealth} | 🛡️ SD: {CurrentShield}/{MaximumShield}",
                _currentHealth, _maximumHealth, _currentShield, _maximumShield);
            UpdateConsoleTitle();
        }

        public void UpdateMaximumHealthShield(uint maximumHealth, uint maximumShield)
        {
            _maximumHealth = Math.Max(1, maximumHealth);
            _maximumShield = maximumShield;
            _logger.LogInformation("❤️ Max HP: {MaximumHealth} | 🛡️ Max SD: {MaximumShield}",
                _maximumHealth, _maximumShield);
            UpdateConsoleTitle();
        }

        public void UpdateCurrentManaAbility(uint currentMana, uint currentAbility)
        {
            _currentMana = currentMana;
            _currentAbility = currentAbility;
            _logger.LogInformation("💧 Mana: {CurrentMana}/{MaximumMana} | ✨ AG: {CurrentAbility}/{MaximumAbility}",
                _currentMana, _maximumMana, _currentAbility, _maximumAbility);
            UpdateConsoleTitle();
        }

        public void UpdateMaximumManaAbility(uint maximumMana, uint maximumAbility)
        {
            _maximumMana = Math.Max(1, maximumMana);
            _maximumAbility = maximumAbility;
            _logger.LogInformation("💧 Max Mana: {MaximumMana} | ✨ Max AG: {MaximumAbility}",
                _maximumMana, _maximumAbility);
            UpdateConsoleTitle();
        }

        public void UpdateStats(ushort strength, ushort agility, ushort vitality, ushort energy, ushort leadership)
        {
            _strength = strength;
            _agility = agility;
            _vitality = vitality;
            _energy = energy;
            _leadership = leadership;
            _logger.LogInformation("📊 Stats: Str={Str}, Agi={Agi}, Vit={Vit}, Ene={Ene}, Cmd={Cmd}",
                _strength, _agility, _vitality, _energy, _leadership);
        }

        public void AddOrUpdatePlayerInScope(ushort maskedId, ushort rawId, byte x, byte y, string name)
        {
            var player = new PlayerScopeObject(maskedId, rawId, x, y, name);
            _objectsInScope.AddOrUpdate(maskedId, player, (_, existing) =>
            {
                existing.PositionX = x;
                existing.PositionY = y;
                ((PlayerScopeObject)existing).Name = name;
                existing.LastUpdate = DateTime.UtcNow;
                // NOTE: RawId is init-only, no need to update here if it doesn't change for existing objects
                return existing;
            });
            _logger.LogTrace("Scope Add/Update: Player {Name} ({Id:X4}, Raw: {RawId:X4}) at [{X},{Y}]", name, maskedId, rawId, x, y);
        }

        public void AddOrUpdateNpcInScope(ushort maskedId, ushort rawId, byte x, byte y, ushort typeNumber, string? name = null)
        {
            var npc = new NpcScopeObject(maskedId, rawId, x, y, typeNumber, name);
            _objectsInScope.AddOrUpdate(maskedId, npc, (_, existing) =>
            {
                existing.PositionX = x;
                existing.PositionY = y;
                ((NpcScopeObject)existing).TypeNumber = typeNumber;
                ((NpcScopeObject)existing).Name = name;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
            _logger.LogTrace("Scope Add/Update: NPC Type {Type} ({Id:X4}, Raw: {RawId:X4}) at [{X},{Y}]", typeNumber, maskedId, rawId, x, y);
        }

        public void AddOrUpdateItemInScope(ushort maskedId, ushort rawId, byte x, byte y, ReadOnlySpan<byte> itemData)
        {
            var item = new ItemScopeObject(maskedId, rawId, x, y, itemData);
            _objectsInScope.AddOrUpdate(maskedId, item, (_, existing) =>
            {
                existing.PositionX = x;
                existing.PositionY = y;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
            _logger.LogTrace("Scope Add/Update: Item ({Id:X4}, Raw: {RawId:X4}) at [{X},{Y}]", maskedId, rawId, x, y);
        }

        public void AddOrUpdateMoneyInScope(ushort maskedId, ushort rawId, byte x, byte y, uint amount)
        {
            var money = new MoneyScopeObject(maskedId, rawId, x, y, amount);
            _objectsInScope.AddOrUpdate(maskedId, money, (_, existing) =>
            {
                existing.PositionX = x;
                existing.PositionY = y;
                ((MoneyScopeObject)existing).Amount = amount;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
            _logger.LogTrace("Scope Add/Update: Money ({Id:X4}, Raw: {RawId:X4}) Amount {Amount} at [{X},{Y}]", maskedId, rawId, amount, x, y);
        }

        /// <summary>
        /// Removes an object from the scope dictionary.
        /// </summary>
        /// <param name="id">The masked ID of the object to remove.</param>
        /// <returns>True if the object was found and removed; otherwise, false.</returns>
        public bool RemoveObjectFromScope(ushort id)
        {
            // Use TryRemove which returns true if the item was found and removed
            if (_objectsInScope.TryRemove(id, out var removedObject))
            {
                _logger.LogTrace("🔭 Scope Remove: ID {Id:X4} ({Type}) - Success", id, removedObject.ObjectType);
                return true; // Return true on successful removal
            }
            else
            {
                _logger.LogTrace("🔭 Scope Remove: ID {Id:X4} - Failed (Not Found)", id);
                return false; // Return false if the object was not found
            }
        }

        /// <summary>
        /// Gets a snapshot of objects of a specific type currently in scope.
        /// </summary>
        /// <param name="type">The type of object to retrieve.</param>
        /// <returns>An enumerable of scope objects matching the type.</returns>
        public IEnumerable<ScopeObject> GetScopeItems(ScopeObjectType type)
        {
            // Return a snapshot to avoid issues with collection modification during iteration
            return _objectsInScope.Values.Where(obj => obj.ObjectType == type).ToList();
        }

        public void ClearScope(bool clearSelf = false)
        {
            if (clearSelf)
            {
                _objectsInScope.Clear();
                _logger.LogInformation("🔭 Scope Cleared (All).");
            }
            else
            {
                // Keep self, remove others
                if (_characterId != 0xFFFF && _objectsInScope.TryGetValue(_characterId, out var self))
                {
                    _objectsInScope.Clear();
                    _objectsInScope.TryAdd(_characterId, self);
                    _logger.LogInformation("🔭 Scope Cleared (Others). Kept Self ({Id:X4})", _characterId);
                }
                else
                {
                    _objectsInScope.Clear();
                    _logger.LogInformation("🔭 Scope Cleared (All - Self ID Unknown).");
                }
            }
        }

        public void ListObjectsInScope()
        {
            Console.WriteLine("--- Objects in Scope ---");
            if (_objectsInScope.IsEmpty)
            {
                Console.WriteLine(" (Scope is empty)");
                return;
            }

            foreach (var kvp in _objectsInScope.OrderBy(o => o.Key)) // Order by ID for consistency
            {
                Console.WriteLine($"  {kvp.Value}"); // Use the ToString() override
            }
            Console.WriteLine($"--- Total: {_objectsInScope.Count} ---");
        }

        public bool ScopeContains(ushort id)
        {
            return _objectsInScope.ContainsKey(id);
        }

        public bool TryUpdateScopeObjectPosition(ushort id, byte x, byte y)
        {
            if (_objectsInScope.TryGetValue(id, out ScopeObject? scopeObject))
            {
                scopeObject.PositionX = x;
                scopeObject.PositionY = y;
                scopeObject.LastUpdate = DateTime.UtcNow;
                return true;
            }
            return false;
        }

        public void SignalMovementHandledIfWalking()
        {
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Releasing walk lock due to error or unexpected state.");
                _isWalking = false;
            }
        }

        private void UpdateConsoleTitle()
        {
            try
            {
                string stateInfo = _currentState switch
                {
                    ClientConnectionState.InGame => $"HP: {_currentHealth}/{_maximumHealth} | SD: {_currentShield}/{_maximumShield} | Mana: {_currentMana}/{_maximumMana} | AG: {_currentAbility}/{_maximumAbility}",
                    ClientConnectionState.ReceivedServerList => "Select Server",
                    ClientConnectionState.ConnectedToGameServer => "Select Character",
                    _ => _currentState.ToString()
                };
                Console.Title = $"MU Client - {_characterName} | {stateInfo}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update console title.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel(); // Ensure cancellation is requested
            await _connectionManager.DisposeAsync();
            _cancellationTokenSource?.Dispose();
            _logger.LogInformation("🛑 Client stopped.");
        }
    }
}