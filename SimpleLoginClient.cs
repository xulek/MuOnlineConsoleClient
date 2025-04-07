using System.Text;
using Microsoft.Extensions.Logging;
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
    /// Main client class managing connection, login, character handling and packet routing.
    /// </summary>
    public sealed class SimpleLoginClient : IAsyncDisposable
    {
        private const string DefaultHost = "192.168.55.220";
        private const int DefaultPort = 55901;
        private const string DefaultUsername = "xulek";
        private const string DefaultPassword = "test1234";
        private const TargetProtocolVersion DefaultTargetVersion = TargetProtocolVersion.Season6;

        private static readonly byte[] ClientVersion = Encoding.ASCII.GetBytes("1.04d");
        private static readonly byte[] ClientSerial = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        private static readonly SimpleModulusKeys EncryptKeys = PipelinedSimpleModulusEncryptor.DefaultClientKey;
        private static readonly SimpleModulusKeys DecryptKeys = PipelinedSimpleModulusDecryptor.DefaultClientKey;
        private static readonly byte[] Xor3Keys = DefaultKeys.Xor3Keys;

        private readonly ILogger<SimpleLoginClient> _logger;
        private readonly ConnectionManager _connectionManager;
        private readonly LoginService _loginService;
        private readonly CharacterService _characterService;
        private readonly PacketRouter _packetRouter;

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

        public bool IsInGame => _isInGame;
        public bool IsConnected => _connectionManager.IsConnected;
        public ushort GetCharacterId() => _characterId;
        public string GetCharacterName() => _characterName;

        public SimpleLoginClient(ILoggerFactory loggerFactory, MuOnlineSettings settings)
        {
            _logger = loggerFactory.CreateLogger<SimpleLoginClient>();

            var clientVersionBytes = Encoding.ASCII.GetBytes(settings.ClientVersion);
            var clientSerialBytes = Encoding.ASCII.GetBytes(settings.ClientSerial);
            var targetVersion = Enum.Parse<TargetProtocolVersion>(settings.ProtocolVersion, ignoreCase: true);
            

            _connectionManager = new ConnectionManager(loggerFactory, settings.Host, settings.Port, EncryptKeys, DecryptKeys);
            _loginService = new LoginService(_connectionManager, _logger, clientVersionBytes, clientSerialBytes, Xor3Keys);
            _characterService = new CharacterService(_connectionManager, _logger);
            _packetRouter = new PacketRouter(loggerFactory.CreateLogger<PacketRouter>(), _characterService, _loginService, targetVersion, this);
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

            if (await _connectionManager.ConnectAsync(cancellationToken))
            {
                _connectionManager.Connection.PacketReceived += _packetRouter.RoutePacketAsync;
                _connectionManager.Connection.Disconnected += _packetRouter.OnDisconnected;

                var commandLoopTask = Task.Run(() => CommandLoopAsync(cancellationToken), cancellationToken);

                await _loginService.SendLoginRequestAsync(_username, _password);
                _logger.LogInformation("🎮 Client started. Type 'exit' to quit or 'move X Y' to move (after entering the game).");

                try
                {
                    await commandLoopTask;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("⌨️ Command loop canceled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "💥 Error in command loop.");
                }
            }
            else
            {
                _logger.LogError("❌ Connection failed.");
            }

            _logger.LogInformation("Shutting down client...");
        }

        private async Task CommandLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⌨️ Command loop started. Type 'exit' to quit.");
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                string? commandLine = null;
                try
                {
                    commandLine = await Console.In.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Error reading command."); break; }

                if (cancellationToken.IsCancellationRequested || commandLine == null) break;

                var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var command = parts[0].ToLowerInvariant();

                switch (command)
                {
                    case "exit":
                        _logger.LogInformation("Received 'exit' command. Shutting down...");
                        _cancellationTokenSource?.Cancel();
                        return;

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
                                    byte animationNumber = 0;

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
                                    byte animationNumber = 0;

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

                    default:
                        _logger.LogWarning("❓ Unknown command: {Command}", command);
                        break;
                }
            }
            _logger.LogInformation("⌨️ Command loop ended.");
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
                    break;
                }

                byte bestDirection = 0xFF;

                if (dx > 0 && dy > 0) bestDirection = 3;
                else if (dx < 0 && dy > 0) bestDirection = 1;
                else if (dx > 0 && dy < 0) bestDirection = 5;
                else if (dx < 0 && dy < 0) bestDirection = 7;
                else if (dx > 0) bestDirection = 4;
                else if (dx < 0) bestDirection = 0;
                else if (dy > 0) bestDirection = 2;
                else if (dy < 0) bestDirection = 6;

                if (bestDirection <= 7)
                {
                    path.Add(bestDirection);

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
                    break;
                }
            }

            return path.ToArray();
        }

        private (byte X, byte Y) CalculateEndPosition(byte startX, byte startY, byte[] path)
        {
            int currentX = startX;
            int currentY = startY;

            foreach (byte direction in path)
            {
                switch (direction)
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
                currentX = Math.Clamp(currentX, 0, 255);
                currentY = Math.Clamp(currentY, 0, 255);
            }

            return ((byte)currentX, (byte)currentY);
        }

        public void SetInGameStatus(bool inGame)
        {
            bool changed = _isInGame != inGame;
            _isInGame = inGame;
            if (changed)
            {
                if (inGame)
                {
                    _logger.LogInformation("🟢 Character is now in-game. You can enter commands (e.g., 'move X Y').");
                }
                else
                {
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

        private void UpdateConsoleTitle()
        {
            try
            {
                Console.Title = $"MU Client - {_characterName} | HP: {_currentHealth}/{_maximumHealth} | SD: {_currentShield}/{_maximumShield} | Mana: {_currentMana}/{_maximumMana} | AG: {_currentAbility}/{_maximumAbility}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update console title.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _connectionManager.DisposeAsync();
            _logger.LogInformation("🛑 Client stopped.");
        }
    }
}