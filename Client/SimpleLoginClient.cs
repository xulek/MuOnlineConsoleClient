using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using MuOnlineConsole.Configuration;
using MuOnlineConsole.Core.Models;
using MuOnlineConsole.Core.Utilities;
using MuOnlineConsole.Networking;
using MuOnlineConsole.Networking.PacketHandling;
using MuOnlineConsole.Networking.Services;

namespace MuOnlineConsole.Client
{
    /// <summary>
    /// Enumeration representing different protocol versions for client compatibility.
    /// </summary>
    public enum TargetProtocolVersion
    {
        Season6,
        Version097,
        Version075
    }

    /// <summary>
    /// Enumeration defining the connection states of the client.
    /// </summary>
    public enum ClientConnectionState
    {
        Initial, // Initial state before any connection attempt
        ConnectingToConnectServer, // Attempting to connect to the Connect Server
        ConnectedToConnectServer, // Successfully connected to the Connect Server
        RequestingServerList, // Requesting the list of game servers from the Connect Server
        ReceivedServerList, // Received the list of game servers
        SelectingServer, // User is in the process of selecting a game server
        RequestingConnectionInfo, // Requesting connection information for the selected game server
        ReceivedConnectionInfo, // Received connection information for the game server
        ConnectingToGameServer, // Attempting to connect to the Game Server
        ConnectedToGameServer, // Successfully connected to the Game Server, ready for login
        Authenticating, // Client is authenticating with the Game Server
        SelectingCharacter, // Client is selecting a character to play
        InGame, // Client is in the game world
        Disconnected // Client is disconnected from all servers
    }

    /// <summary>
    /// Main client class responsible for managing the connection, login process, character handling, and packet routing.
    /// Implements IAsyncDisposable for proper resource cleanup.
    /// </summary>
    public sealed class SimpleLoginClient : IAsyncDisposable
    {
        /// <summary>
        /// Encryption keys used for communication with the server.
        /// </summary>
        private static readonly SimpleModulusKeys EncryptKeys = PipelinedSimpleModulusEncryptor.DefaultClientKey;
        /// <summary>
        /// Decryption keys used for communication with the server.
        /// </summary>
        private static readonly SimpleModulusKeys DecryptKeys = PipelinedSimpleModulusDecryptor.DefaultClientKey;
        /// <summary>
        /// XOR3 keys used for packet encryption/decryption.
        /// </summary>
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

        private ClientConnectionState _currentState = ClientConnectionState.Initial;
        private List<ServerInfo> _serverList = new();
        private List<(string Name, CharacterClassNumber Class)>? _pendingCharacterSelection = null;
        private bool _isWalking = false; // Flag to indicate if the character is currently performing a walk action
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Dictionary<byte, byte> _serverDirectionMap; // Map for translating client-side directions to server-side directions

        /// <summary>
        /// Indicates if the client is currently in the game world.
        /// </summary>
        public bool IsInGame => _characterState.IsInGame;
        /// <summary>
        /// Gets the ID of the currently selected character.
        /// </summary>
        public ushort GetCharacterId() => _characterState.Id;
        /// <summary>
        /// Gets the name of the currently selected character.
        /// </summary>
        public string GetCharacterName() => _characterState.Name;
        /// <summary>
        /// Indicates if the client is currently connected to a server.
        /// </summary>
        public bool IsConnected => _connectionManager.IsConnected;
        /// <summary>
        /// Indicates if the last item pickup attempt was successful.
        /// </summary>
        public bool LastPickupSucceeded { get; set; } = false; // TODO: Consider moving this state if pickup logic is extracted
        /// <summary>
        /// Indicates if the pickup action has been handled by the server response.
        /// </summary>
        public bool PickupHandled { get; set; } = false; // TODO: Consider moving this state if pickup logic is extracted

        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleLoginClient"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory used for creating loggers.</param>
        /// <param name="settings">The settings for the Mu Online client.</param>
        public SimpleLoginClient(ILoggerFactory loggerFactory, MuOnlineSettings settings)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SimpleLoginClient>();
            _settings = settings;

            var clientVersionBytes = Encoding.ASCII.GetBytes(settings.ClientVersion);
            var clientSerialBytes = Encoding.ASCII.GetBytes(settings.ClientSerial);
            var targetVersion = System.Enum.Parse<TargetProtocolVersion>(settings.ProtocolVersion, ignoreCase: true);

            // Initialize state and managers first to ensure they are available for services and packet router.
            _characterState = new CharacterState(_loggerFactory);
            _scopeManager = new ScopeManager(_loggerFactory, _characterState);

            // Initialize services which handle specific network operations.
            _connectionManager = new ConnectionManager(loggerFactory, EncryptKeys, DecryptKeys);
            _loginService = new LoginService(_connectionManager, _loggerFactory.CreateLogger<LoginService>(), clientVersionBytes, clientSerialBytes, Xor3Keys);
            _characterService = new CharacterService(_connectionManager, _loggerFactory.CreateLogger<CharacterService>());
            _connectServerService = new ConnectServerService(_connectionManager, _loggerFactory.CreateLogger<ConnectServerService>());

            // Initialize PacketRouter, which is responsible for routing incoming packets to appropriate handlers.
            // Passing dependencies needed by its internal handlers.
            _packetRouter = new PacketRouter(loggerFactory, _characterService, _loginService, targetVersion, this, _characterState, _scopeManager, _settings);

            _serverDirectionMap = settings.DirectionMap;
        }

        /// <summary>
        /// Runs the main client execution loop asynchronously.
        /// This includes connecting to the server, handling commands, and managing the client lifecycle.
        /// </summary>
        public async Task RunAsync()
        {
            _logger.LogInformation("🚀 Starting client execution (Target: {Version})...", _packetRouter.TargetVersion);
            _logger.LogInformation("🔍 Using username '{Username}' and password '{Password}'", _settings.Username, _settings.Password);
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            var commandLoopTask = Task.Run(() => CommandLoopAsync(cancellationToken), cancellationToken);

            await ConnectToConnectServerAsync(cancellationToken);

            try
            {
                await commandLoopTask;
                if (!cancellationToken.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
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
                    _cancellationTokenSource.Cancel();
                }
            }

            _logger.LogInformation("Shutting down client...");
        }

        /// <summary>
        /// Establishes a connection to the Connect Server asynchronously.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token that can be used to cancel the operation.</param>
        private async Task ConnectToConnectServerAsync(CancellationToken cancellationToken)
        {
            _currentState = ClientConnectionState.ConnectingToConnectServer;
            _packetRouter.SetRoutingMode(true); // Enable routing for Connect Server packets

            if (await _connectionManager.ConnectAsync(_settings.ConnectServerHost, _settings.ConnectServerPort, false, cancellationToken))
            {
                _currentState = ClientConnectionState.ConnectedToConnectServer;
                _connectionManager.Connection.PacketReceived += HandlePacketAsync; // Subscribe to packet received event
                _connectionManager.Connection.Disconnected += HandleDisconnectAsync; // Subscribe to disconnected event

                _logger.LogInformation("✅ Successfully connected to Connect Server. Requesting server list...");
                _currentState = ClientConnectionState.RequestingServerList;
                await _connectServerService.RequestServerListAsync(); // Request the server list from the Connect Server
            }
            else
            {
                _logger.LogError("❌ Connection to Connect Server failed.");
                _currentState = ClientConnectionState.Disconnected;
                _cancellationTokenSource?.Cancel(); // Cancel the client operation if connection fails
            }
        }

        /// <summary>
        /// Stores the received server list and updates the client state.
        /// </summary>
        /// <param name="servers">The list of servers received from the Connect Server.</param>
        public void StoreServerList(List<ServerInfo> servers)
        {
            _serverList = servers;
            _currentState = ClientConnectionState.ReceivedServerList;
            _logger.LogInformation("📝 Server list received with {Count} servers.", servers.Count);
            DisplayServerList(); // Display the received server list to the console
        }

        /// <summary>
        /// Displays the list of available servers in the console.
        /// </summary>
        private void DisplayServerList()
        {
            Console.WriteLine("=== Available Servers ===");
            if (_serverList.Count == 0)
            {
                Console.WriteLine("No servers available.");
            }
            else
            {
                for (int i = 0; i < _serverList.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. ID: {_serverList[i].ServerId}, Load: {_serverList[i].LoadPercentage}%");
                }
            }
            Console.WriteLine("👉 Type 'connect <number>' (e.g., 'connect 1') to connect to a server.");
            Console.WriteLine("👉 Or type 'refresh' to request the server list again.");
        }

        /// <summary>
        /// Handles the server selection process based on the server ID.
        /// </summary>
        /// <param name="serverId">The ID of the server selected by the user.</param>
        private async Task HandleServerSelectionAsync(ushort serverId)
        {
            if (_currentState != ClientConnectionState.ReceivedServerList)
            {
                _logger.LogWarning("⚠️ Cannot select server, not in the correct state ({State}).", _currentState);
                return;
            }

            _logger.LogInformation("👉 Requesting connection details for Server ID {ServerId}...", serverId);
            _currentState = ClientConnectionState.RequestingConnectionInfo;
            await _connectServerService.RequestConnectionInfoAsync(serverId); // Request connection info for the selected server
        }

        /// <summary>
        /// Switches the client connection from the Connect Server to the Game Server.
        /// </summary>
        /// <param name="host">The host address of the Game Server.</param>
        /// <param name="port">The port number of the Game Server.</param>
        public async void SwitchToGameServer(string host, int port)
        {
            if (_currentState != ClientConnectionState.RequestingConnectionInfo)
            {
                _logger.LogWarning("⚠️ Received game server info in unexpected state ({State}). Ignoring.", _currentState);
                return;
            }
            _currentState = ClientConnectionState.ReceivedConnectionInfo;

            _logger.LogInformation("🔌 Disconnecting from Connect Server...");

            var oldConnection = _connectionManager.Connection;
            if (oldConnection != null)
            {
                oldConnection.PacketReceived -= HandlePacketAsync; // Unsubscribe from packet received event of the old connection
                oldConnection.Disconnected -= HandleDisconnectAsync; // Unsubscribe from disconnected event of the old connection
                _logger.LogDebug("Unsubscribed event handlers from old Connect Server connection.");
            }

            await _connectionManager.DisconnectAsync(); // Disconnect from the Connect Server

            _logger.LogInformation("🔌 Connecting to Game Server {Host}:{Port}...", host, port);
            _currentState = ClientConnectionState.ConnectingToGameServer;
            _packetRouter.SetRoutingMode(false); // Disable routing for Connect Server packets, enable for Game Server

            if (await _connectionManager.ConnectAsync(host, port, true, _cancellationTokenSource?.Token ?? default))
            {
                _currentState = ClientConnectionState.ConnectedToGameServer;

                if (_connectionManager.Connection != null)
                {
                    _connectionManager.Connection.PacketReceived += HandlePacketAsync; // Subscribe to packet received event for the new connection
                    _connectionManager.Connection.Disconnected += HandleDisconnectAsync; // Subscribe to disconnected event for the new connection
                    _logger.LogDebug("Subscribed event handlers to new Game Server connection.");
                    _logger.LogInformation("✅ Successfully connected to Game Server. Ready to login.");
                    // F1 00 handler in PacketRouter will trigger SendLoginRequest upon successful connection
                }
                else
                {
                    _logger.LogError("❌ Failed to get new connection object after connecting to Game Server.");
                    _currentState = ClientConnectionState.Disconnected;
                    _cancellationTokenSource?.Cancel();
                }
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
        /// </summary>
        public void SendLoginRequest()
        {
            if (_currentState != ClientConnectionState.ConnectedToGameServer)
            {
                _logger.LogWarning("⚠️ Cannot send login request, not connected to Game Server or in wrong state ({State}).", _currentState);
                return;
            }
            _currentState = ClientConnectionState.Authenticating;
            Task.Run(() => _loginService.SendLoginRequestAsync(_settings.Username, _settings.Password)); // Send login request in a separate task
        }

        /// <summary>
        /// Handles incoming packets by routing them to the PacketRouter.
        /// </summary>
        /// <param name="sequence">The read-only sequence of bytes representing the received packet.</param>
        /// <returns>A ValueTask representing the asynchronous operation.</returns>
        private ValueTask HandlePacketAsync(ReadOnlySequence<byte> sequence)
        {
            return new ValueTask(_packetRouter.RoutePacketAsync(sequence)); // Route the received packet
        }

        /// <summary>
        /// Handles disconnection events by notifying the PacketRouter.
        /// </summary>
        /// <returns>A ValueTask representing the asynchronous operation.</returns>
        private ValueTask HandleDisconnectAsync()
        {
            return new ValueTask(_packetRouter.OnDisconnected()); // Notify packet router about disconnection
        }

        /// <summary>
        /// Presents the character selection list to the user in the console and prepares for character selection.
        /// </summary>
        /// <param name="characters">The list of available characters for the account.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public Task SelectCharacterInteractivelyAsync(List<(string Name, CharacterClassNumber Class)> characters)
        {
            if (characters.Count == 0)
            {
                _logger.LogWarning("⚠️ No characters available on the account.");
                return Task.CompletedTask;
            }

            Console.WriteLine("🧍 Available characters:");
            for (int i = 0; i < characters.Count; i++)
            {
                // Use Item1 for name, Item2 for class when accessing the tuple from the list
                string className = CharacterClassDatabase.GetClassName(characters[i].Class); // Accessing Item2 via named property is fine here
                Console.WriteLine($"  {i + 1}. {characters[i].Name} ({className})"); // Accessing Item1 via named property is fine here
            }

            Console.WriteLine("👉 Type: select <number>  (e.g. 'select 1') to choose a character.");
            _pendingCharacterSelection = characters; // Store the character list for selection
            return Task.CompletedTask;
        }

        /// <summary>
        /// Requests the server list from the Connect Server.
        /// </summary>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public Task RequestServerList()
        {
            if (_currentState != ClientConnectionState.ConnectedToConnectServer && _currentState != ClientConnectionState.ReceivedServerList)
            {
                _logger.LogWarning("⚠️ Cannot request server list, not connected to Connect Server or in wrong state ({State}).", _currentState);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Requesting server list...");
            _currentState = ClientConnectionState.RequestingServerList;
            return _connectServerService.RequestServerListAsync(); // Initiate server list request
        }

        /// <summary>
        /// Signals that a movement action has been processed, unlocking the walk command.
        /// </summary>
        public void SignalMovementHandled()
        {
            if (_isWalking)
            {
                _logger.LogDebug("🚶 Movement processed (or failed), unlocking walk command.");
                _isWalking = false; // Reset walking flag
            }
        }

        /// <summary>
        /// Signals that a movement action has been handled, even if walking flag was set due to error or unexpected state.
        /// </summary>
        public void SignalMovementHandledIfWalking()
        {
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Releasing walk lock due to error or unexpected state.");
                _isWalking = false; // Reset walking flag
            }
        }

        /// <summary>
        /// Asynchronous command processing loop, reads commands from the console.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token that can be used to cancel the operation.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task CommandLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⌨️ Command loop started. Type 'help' for commands, 'exit' to quit.");
            while (!cancellationToken.IsCancellationRequested)
            {
                string? commandLine = null;
                try
                {
                    commandLine = await Console.In.ReadLineAsync(cancellationToken); // Read command line input asynchronously
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex) { _logger.LogError(ex, "Error reading command line input (IO)."); break; }
                catch (Exception ex) { _logger.LogError(ex, "Error reading command line input."); break; }

                if (cancellationToken.IsCancellationRequested || commandLine == null) break;

                await ProcessCommandAsync(commandLine); // Process the entered command
            }
            _logger.LogInformation("⌨️ Command loop ended.");
        }

        /// <summary>
        /// Processes a single command entered by the user.
        /// </summary>
        /// <param name="commandLine">The command line string entered by the user.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task ProcessCommandAsync(string commandLine)
        {
            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries); // Split command line into parts
            if (parts.Length == 0) return;

            var command = parts[0].ToLowerInvariant(); // Get the command, convert to lowercase
            var args = parts.Skip(1).ToArray(); // Get the arguments

            // General commands available in any state
            switch (command)
            {
                case "exit":
                    _logger.LogInformation("Received 'exit' command. Shutting down...");
                    _cancellationTokenSource?.Cancel(); // Cancel the client operation
                    return;
                case "help":
                    DisplayHelp(); // Display help information
                    return;
            }

            // State-specific commands, handle commands based on the current client state
            if (_currentState < ClientConnectionState.ConnectingToGameServer)
            {
                await HandleConnectServerCommandAsync(command, args); // Handle commands valid in Connect Server state
            }
            else if (_currentState >= ClientConnectionState.ConnectedToGameServer)
            {
                await HandleGameServerCommandAsync(command, args); // Handle commands valid in Game Server state
            }
            else // Disconnected or Initial state
            {
                _logger.LogWarning("❓ Not connected. Available commands: 'exit', 'help'.");
            }
        }

        /// <summary>
        /// Handles commands that are valid when connected to the Connect Server.
        /// </summary>
        /// <param name="command">The command entered by the user.</param>
        /// <param name="args">The arguments for the command.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleConnectServerCommandAsync(string command, string[] args)
        {
            switch (command)
            {
                case "servers":
                case "list":
                    if (_serverList.Count > 0)
                    {
                        DisplayServerList(); // Display the server list if available
                    }
                    else
                    {
                        _logger.LogWarning("No server list available. Use 'refresh'.");
                    }
                    break;

                case "refresh":
                    if (_currentState == ClientConnectionState.ConnectedToConnectServer || _currentState == ClientConnectionState.ReceivedServerList)
                    {
                        await RequestServerList(); // Request server list refresh
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
                    if (args.Length != 1 || !int.TryParse(args[0], out int index) || index < 1 || index > _serverList.Count)
                    {
                        _logger.LogWarning("Usage: connect <number> (1-{Max})", _serverList.Count);
                        break;
                    }
                    var selectedServer = _serverList[index - 1]; // Get the selected server from the list
                    await HandleServerSelectionAsync(selectedServer.ServerId); // Handle server selection
                    break;

                default:
                    _logger.LogWarning("❓ Unknown command or command not valid in current state ({State}): {Command}", _currentState, command);
                    break;
            }
        }

        /// <summary>
        /// Handles commands that are valid when connected to the Game Server and in-game.
        /// </summary>
        /// <param name="command">The command entered by the user.</param>
        /// <param name="args">The arguments for the command.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleGameServerCommandAsync(string command, string[] args)
        {
            switch (command)
            {
                case "select":
                    await HandleSelectCharacterCommandAsync(args); // Handle character selection command
                    break;
                case "nearby":
                case "scope":
                    Console.WriteLine(_scopeManager.GetScopeListDisplay()); // Display objects in scope
                    break; // Use ScopeManager display
                case "move":
                    await HandleMoveCommandAsync(args); // Handle instant move command
                    break;
                case "walkto":
                    await HandleWalkToCommandAsync(args); // Handle walk to coordinates command
                    break;
                case "walk":
                    await HandleWalkCommandAsync(args); // Handle walk in directions command
                    break;
                case "pickup":
                    await HandlePickupCommandAsync(args); // Handle item pickup command
                    break;
                case "stats":
                    if (!IsInGame)
                    {
                        _logger.LogWarning("Cannot display stats - character not in game.");
                    }
                    else
                    {
                        Console.WriteLine(_characterState.GetStatsDisplay()); // Display character stats
                    }
                    break;
                case "inv":
                case "inventory":
                    if (!IsInGame)
                    {
                        _logger.LogWarning("Cannot display inventory - character not in game.");
                    }
                    else
                    {
                        Console.WriteLine(_characterState.GetInventoryDisplay()); // Display character inventory
                    }
                    break;
                case "skills": // NEW COMMAND
                    if (!IsInGame) _logger.LogWarning("Cannot display skills - character not in game.");
                    else Console.WriteLine(_characterState.GetSkillListDisplay()); // Display character skills
                    break;
                default:
                    _logger.LogWarning("❓ Unknown command or command not valid in current state ({State}): {Command}", _currentState, command);
                    break;
            }
        }

        /// <summary>
        /// Handles the character selection command.
        /// </summary>
        /// <param name="args">The arguments for the command (character index).</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleSelectCharacterCommandAsync(string[] args)
        {
            if (_pendingCharacterSelection == null)
            {
                _logger.LogWarning("No character list available to select from.");
                return;
            }
            if (args.Length != 1 || !int.TryParse(args[0], out int index) || index < 1 || index > _pendingCharacterSelection.Count)
            {
                _logger.LogWarning("Usage: select <number> (1-{Max})", _pendingCharacterSelection.Count);
                return;
            }

            // Retrieve the selected character tuple
            var selectedCharacterInfo = _pendingCharacterSelection[index - 1];

            // Access tuple elements using Item1 and Item2
            var selectedName = selectedCharacterInfo.Item1;
            var selectedClass = selectedCharacterInfo.Item2;

            _logger.LogInformation("🎯 Selected character: {Name} ({Class})", selectedName, CharacterClassDatabase.GetClassName(selectedClass));

            // Update CharacterState with selected character info
            _characterState.Name = selectedName;
            _characterState.Class = selectedClass; // Set the class

            _currentState = ClientConnectionState.SelectingCharacter;
            await _characterService.SelectCharacterAsync(selectedName); // Send character selection request
            _pendingCharacterSelection = null; // Clear pending character selection list
            UpdateConsoleTitle(); // Update console title to reflect character selection
        }


        /// <summary>
        /// Handles the instant move command.
        /// </summary>
        /// <param name="args">The arguments for the command (X and Y coordinates).</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleMoveCommandAsync(string[] args)
        {
            if (!IsInGame)
            {
                _logger.LogWarning("Cannot move - character not in game.");
                return;
            }
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Character is currently walking, cannot use 'move'.");
                return;
            }
            if (args.Length == 2 && byte.TryParse(args[0], out byte x) && byte.TryParse(args[1], out byte y))
            {
                await _characterService.SendInstantMoveRequestAsync(x, y); // Send instant move request
            }
            else
            {
                _logger.LogWarning("Invalid 'move' command format. Use: move X Y");
            }
        }

        /// <summary>
        /// Handles the walk to coordinates command. Generates a path and starts the walk sequence.
        /// </summary>
        /// <param name="args">The arguments for the command (target X and Y coordinates).</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleWalkToCommandAsync(string[] args)
        {
            if (!IsInGame)
            {
                _logger.LogWarning("Cannot walk - character is not in game.");
                return;
            }
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Character is already moving, please wait.");
                return;
            }
            if (args.Length == 2 && byte.TryParse(args[0], out byte targetX) && byte.TryParse(args[1], out byte targetY))
            {
                byte startX = _characterState.PositionX; // Use CharacterState position as starting point
                byte startY = _characterState.PositionY; // Use CharacterState position as starting point
                var generatedPath = GenerateSimplePathTowards(startX, startY, targetX, targetY); // Generate path to target coordinates
                if (generatedPath.Length > 0)
                {
                    await StartWalkSequenceAsync(generatedPath, targetX, targetY); // Start the walk sequence
                }
                else
                {
                    _logger.LogInformation("🚶 Already at target location or no path.");
                }
            }
            else
            {
                _logger.LogWarning("Invalid 'walkto' format. Use: walkto X Y");
            }
        }

        /// <summary>
        /// Handles the walk in directions command. Starts a walk sequence based on provided directions.
        /// </summary>
        /// <param name="args">The arguments for the command (directions 0-7).</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandleWalkCommandAsync(string[] args)
        {
            if (!IsInGame)
            {
                _logger.LogWarning("Cannot walk - character is not in game.");
                return;
            }
            if (_isWalking)
            {
                _logger.LogWarning("🚶 Character is already moving, please wait.");
                return;
            }
            if (args.Length > 0)
            {
                var directions = args.Select(p => byte.TryParse(p, out byte dir) && dir <= 7 ? (byte?)dir : null).Where(d => d.HasValue).Select(d => d.Value).ToArray(); // Parse directions from arguments
                if (directions.Length > 0)
                {
                    await StartWalkSequenceAsync(directions); // Start walk sequence with provided directions
                }
                else
                {
                    _logger.LogWarning("Invalid 'walk' arguments. Use numeric directions 0-7.");
                }
            }
            else
            {
                _logger.LogWarning("Invalid 'walk' command format. Use: walk <dir1> [dir2] ...");
                _logger.LogInformation("Directions: 0:W, 1:SW, 2:S, 3:SE, 4:E, 5:NE, 6:N, 7:NW");
            }
        }

        /// <summary>
        /// Starts a walk sequence by sending animation and walk requests to the server.
        /// </summary>
        /// <param name="path">The array of directions for the walk sequence.</param>
        /// <param name="targetX">Optional target X coordinate for logging purposes.</param>
        /// <param name="targetY">Optional target Y coordinate for logging purposes.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task StartWalkSequenceAsync(byte[] path, byte? targetX = null, byte? targetY = null)
        {
            var translatedPath = path.Select(TranslateDirection).ToArray(); // Translate directions to server directions
            if (translatedPath.Length == 0) return;
            _isWalking = true; // Set walking flag to prevent concurrent walk commands
            try
            {
                byte firstTranslatedStep = translatedPath[0];
                byte animationNumber = 0;
                _logger.LogDebug("🚶 Sending AnimationRequest before walk. Translated Direction: {Dir}, Anim: {Anim}", firstTranslatedStep, animationNumber);
                await _characterService.SendAnimationRequestAsync(firstTranslatedStep, animationNumber); // Send animation request

                if (targetX.HasValue && targetY.HasValue)
                {
                    _logger.LogDebug("🚶 Sending WalkRequest. Client Position: ({CurrentX},{CurrentY}), Target: ({TargetX},{TargetY}), Original Path: [{OrigPath}], Translated Path: [{TransPath}]", _characterState.PositionX, _characterState.PositionY, targetX.Value, targetY.Value, string.Join(",", path), string.Join(",", translatedPath));
                }
                else
                {
                    _logger.LogInformation("🚶 Sending WalkRequest packet with start ({StartX},{StartY}), {Steps} steps (translated)...", _characterState.PositionX, _characterState.PositionY, translatedPath.Length);
                }
                await _characterService.SendWalkRequestAsync(_characterState.PositionX, _characterState.PositionY, translatedPath); // Send walk request with translated path
            }
            catch (OperationCanceledException)
            {
                _isWalking = false; // Reset walking flag on cancellation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during walk sequence.");
                _isWalking = false; // Reset walking flag on error
            }
        }

        /// <summary>
        /// Handles the item pickup command. Attempts to pick up an item by ID or the nearest item.
        /// </summary>
        /// <param name="args">The arguments for the command (item ID or 'near').</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task HandlePickupCommandAsync(string[] args)
        {
            if (!IsInGame)
            {
                _logger.LogWarning("Cannot pick up item - character not in game.");
                return;
            }
            if (args.Length == 1)
            {
                ushort? targetItemIdRaw = null;
                string inputIdString = args[0];
                if (inputIdString.Equals("near", StringComparison.OrdinalIgnoreCase))
                {
                    targetItemIdRaw = _scopeManager.FindNearestPickupItemRawId(); // Find nearest item in scope
                    if (!targetItemIdRaw.HasValue)
                    {
                        _logger.LogInformation("No nearby items found to pick up.");
                        return;
                    }
                    _logger.LogInformation("👜 Attempting to pick up nearest item (Raw ID {RawId:X4})...", targetItemIdRaw.Value);
                    await AttemptPickupWithRetryAsync(targetItemIdRaw.Value); // Attempt pickup with retry logic for nearest item
                }
                else if (ushort.TryParse(inputIdString, System.Globalization.NumberStyles.HexNumber, null, out ushort itemIdHexRaw))
                {
                    targetItemIdRaw = itemIdHexRaw;
                    _logger.LogInformation("👜 Attempting to pick up item with Raw ID {ItemId:X4} (parsed as hex from input '{Input}')...", targetItemIdRaw.Value, inputIdString);
                    await SendPickupRequestAsync(targetItemIdRaw.Value); // Send pickup request for item ID (hex input)
                }
                else if (ushort.TryParse(inputIdString, out ushort itemIdDecRaw))
                {
                    targetItemIdRaw = itemIdDecRaw;
                    _logger.LogInformation("👜 Attempting to pick up item with Raw ID {ItemId:X4} (parsed as decimal from input '{Input}')...", targetItemIdRaw.Value, inputIdString);
                    await SendPickupRequestAsync(targetItemIdRaw.Value); // Send pickup request for item ID (decimal input)
                }
                else
                {
                    _logger.LogWarning("Invalid pickup target '{Target}'. Use: 'pickup near' or 'pickup <ItemID_Hex_Or_Dec>'.", inputIdString);
                }
            }
            else
            {
                _logger.LogWarning("Invalid 'pickup' command format. Use: 'pickup near' or 'pickup <ItemID>'.");
            }
        }

        /// <summary>
        /// Attempts to pick up an item with retry logic to handle potential packet loss or server delays.
        /// </summary>
        /// <param name="targetItemIdRaw">The raw item ID of the item to pick up.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task AttemptPickupWithRetryAsync(ushort targetItemIdRaw)
        {
            int attempts = 0;
            const int maxAttempts = 20;
            bool pickupSuccess = false;
            DateTime startTime = DateTime.UtcNow;
            while (!pickupSuccess && attempts < maxAttempts && (DateTime.UtcNow - startTime).TotalSeconds < 20)
            {
                await SendPickupRequestAsync(targetItemIdRaw); // Send pickup request
                await Task.Delay(1000); // Wait for 1 second before retrying
                ushort? currentItemId = _scopeManager.FindNearestPickupItemRawId(); // Check if item is still in scope
                if (!currentItemId.HasValue || currentItemId.Value != targetItemIdRaw)
                {
                    pickupSuccess = true;
                    _logger.LogInformation("✅ Item (Raw ID {RawId:X4}) seems to have been picked up after {Attempts} attempts.", targetItemIdRaw, attempts + 1);
                }
                else
                {
                    _logger.LogDebug("Attempt {AttemptNum}: Item {RawId:X4} still present.", attempts + 1, targetItemIdRaw);
                }
                attempts++;
            }
            if (!pickupSuccess) _logger.LogWarning("⚠️ Failed to pick up item (Raw ID {RawId:X4}) after {Attempts} attempts.", targetItemIdRaw, attempts);
        }

        /// <summary>
        /// Sends a pickup item request packet to the server.
        /// </summary>
        /// <param name="targetItemIdRaw">The raw item ID of the item to pick up.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private async Task SendPickupRequestAsync(ushort targetItemIdRaw)
        {
            ushort itemIdMasked = (ushort)(targetItemIdRaw & 0x7FFF); // Mask item ID
            try
            {
                await _connectionManager.Connection.SendPickupItemRequestAsync(itemIdMasked); // Send pickup item request packet
                _logger.LogInformation("✔️ Pickup request sent for RAW ID {RawID:X4}.", targetItemIdRaw);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error while sending pickup packet for Masked ID {MaskedId:X4}.", itemIdMasked);
            }
        }

        /// <summary>
        /// Displays the help message with available commands in the console.
        /// </summary>
        private void DisplayHelp()
        {
            Console.WriteLine("\n--- Available Commands ---");
            Console.WriteLine(" General:");
            Console.WriteLine("  exit          - Closes the application.");
            Console.WriteLine("  help          - Shows this help message.");
            Console.WriteLine("\n Connect Server State (< ConnectingToGameServer):");
            Console.WriteLine("  servers / list - Displays the available server list.");
            Console.WriteLine("  refresh       - Requests an updated server list.");
            Console.WriteLine("  connect <num> - Connects to the specified server number from the list.");
            Console.WriteLine("\n Game Server State (>= ConnectedToGameServer):");
            Console.WriteLine("  select <num>  - Selects the character by number from the list.");
            Console.WriteLine("  nearby / scope- Lists objects currently in scope.");
            Console.WriteLine("  move <x> <y>  - Instantly moves the character to coordinates (X, Y).");
            Console.WriteLine("  walkto <x> <y>- Walks towards the target coordinates (X, Y).");
            Console.WriteLine("  walk <d> ...  - Walks a sequence of directions (0-7).");
            Console.WriteLine("  pickup <id|near> - Picks up an item by Raw ID (hex/dec) or the nearest item.");
            Console.WriteLine("  stats         - Displays current character statistics.");
            Console.WriteLine("  inv / inventory - Displays current character inventory.");
            Console.WriteLine("  skills        - Displays current character skill list."); // Added help text
            Console.WriteLine("------------------------\n");
        }

        /// <summary>
        /// Translates a client-side direction to a server-side direction using the direction map.
        /// </summary>
        /// <param name="standardDirection">The client-side standard direction (0-7).</param>
        /// <returns>The server-side direction, or the original direction if no translation is found.</returns>
        private byte TranslateDirection(byte standardDirection)
        {
            if (_serverDirectionMap.TryGetValue(standardDirection, out byte serverDirection))
            {
                _logger.LogTrace("Translating direction {StandardDir} -> {ServerDir}", standardDirection, serverDirection);
                return serverDirection; // Return translated server direction
            }
            _logger.LogWarning("Translation not found for direction {StandardDir}, using original.", standardDirection);
            return standardDirection; // Return original direction if no translation found
        }

        /// <summary>
        /// Generates a simple path towards target coordinates using basic direction logic.
        /// </summary>
        /// <param name="startX">The starting X coordinate.</param>
        /// <param name="startY">The starting Y coordinate.</param>
        /// <param name="targetX">The target X coordinate.</param>
        /// <param name="targetY">The target Y coordinate.</param>
        /// <returns>An array of directions representing the generated path.</returns>
        private byte[] GenerateSimplePathTowards(byte startX, byte startY, byte targetX, byte targetY)
        {
            const int MaxStepsPerPacket = 15; // Maximum steps allowed in a single walk packet
            var path = new List<byte>(MaxStepsPerPacket);
            int currentX = startX;
            int currentY = startY;

            for (int i = 0; i < MaxStepsPerPacket; i++)
            {
                int dx = targetX - currentX;
                int dy = targetY - currentY;

                if (dx == 0 && dy == 0) break; // Reached target, stop path generation

                byte bestDirection = 0xFF; // Invalid direction, default value

                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    bestDirection = (dx > 0) ? (byte)4 : (byte)0; // E or W direction based on X difference
                }
                else if (Math.Abs(dy) > Math.Abs(dx))
                {
                    bestDirection = (dy > 0) ? (byte)2 : (byte)6; // S or N direction based on Y difference
                }
                else // Diagonal movement
                {
                    if (dx > 0 && dy > 0) bestDirection = 3; // SE
                    else if (dx < 0 && dy > 0) bestDirection = 1; // SW
                    else if (dx > 0 && dy < 0) bestDirection = 5; // NE
                    else if (dx < 0 && dy < 0) bestDirection = 7; // NW
                }

                if (bestDirection <= 7)
                {
                    path.Add(bestDirection); // Add direction to the path
                    switch (bestDirection) // Update current position based on chosen direction
                    {
                        case 0:
                            currentX--;
                            break;
                        case 1:
                            currentX--;
                            currentY++;
                            break;
                        case 2:
                            currentY++;
                            break;
                        case 3:
                            currentX++;
                            currentY++;
                            break;
                        case 4:
                            currentX++;
                            break;
                        case 5:
                            currentX++;
                            currentY--;
                            break;
                        case 6:
                            currentY--;
                            break;
                        case 7:
                            currentX--;
                            currentY--;
                            break;
                    }
                }
                else
                {
                    _logger.LogWarning("Could not determine direction from ({curX},{curY}) to ({tarX},{tarY})", currentX, currentY, targetX, targetY);
                    break; // Stop path generation if direction cannot be determined
                }
            }
            return path.ToArray(); // Return generated path as byte array
        }

        /// <summary>
        /// Sets the in-game status of the character and updates client state and console title accordingly.
        /// </summary>
        /// <param name="inGame">True if the character is in-game, false otherwise.</param>
        public void SetInGameStatus(bool inGame)
        {
            bool changed = _characterState.IsInGame != inGame;
            _characterState.IsInGame = inGame; // Update CharacterState in-game flag

            if (changed)
            {
                if (inGame)
                {
                    _currentState = ClientConnectionState.InGame; // Update client state to InGame
                    _logger.LogInformation("🟢 Character is now in-game. You can enter commands (e.g., 'move X Y').");
                }
                else
                {
                    // If we were in game and now we are not, it means we left the game world.
                    // Reset state appropriately. If still connected to GS, go back to character selection possibility.
                    // If disconnected, HandleDisconnectAsync will set state to Disconnected.
                    if (_connectionManager.IsConnected) // Check if still connected to Game Server
                    {
                        _currentState = ClientConnectionState.ConnectedToGameServer; // Set state back to ConnectedToGameServer, ready for character select
                        _logger.LogInformation("🚪 Character has left the game world (still connected).");
                    }
                    else
                    {
                        _currentState = ClientConnectionState.Disconnected; // Set state to Disconnected
                        _logger.LogInformation("🚪 Character has left the game world (disconnected).");
                    }
                }
                UpdateConsoleTitle(); // Update console title to reflect in-game status
            }
        }

        /// <summary>
        /// Updates the console title to display relevant client and character information.
        /// </summary>
        public void UpdateConsoleTitle()
        {
            try
            {
                string className = CharacterClassDatabase.GetClassName(_characterState.Class);
                string stateInfo = _currentState switch
                {
                    ClientConnectionState.InGame => $"HP: {_characterState.CurrentHealth}/{_characterState.MaximumHealth} | SD: {_characterState.CurrentShield}/{_characterState.MaximumShield} | Mana: {_characterState.CurrentMana}/{_characterState.MaximumMana} | AG: {_characterState.CurrentAbility}/{_characterState.MaximumAbility}",
                    ClientConnectionState.ReceivedServerList => "Select Server",
                    ClientConnectionState.ConnectedToGameServer => "Select Character",
                    ClientConnectionState.SelectingCharacter => "Selecting Character...",
                    _ => _currentState.ToString() // Default case: display current state as string
                };
                // Include class name in the title
                Console.Title = $"MU Client - {_characterState.Name} ({className}) | {stateInfo}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update console title.");
            }
        }

        /// <summary>
        /// Asynchronously disposes of the client resources.
        /// </summary>
        /// <returns>A ValueTask representing the asynchronous operation.</returns>
        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel(); // Cancel any ongoing operations
            await _connectionManager.DisposeAsync(); // Dispose of the connection manager
            _cancellationTokenSource?.Dispose(); // Dispose of the cancellation token source
            _logger.LogInformation("🛑 Client stopped.");
        }
    }
}