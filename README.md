# MU Online Console Client (.NET Core)

A modern, modular C# client for connecting, logging in, and interacting with a MU Online server. Built upon the `MUnique.OpenMU.Network` library, featuring full packet parsing and protocol version handling.

## Features

* Connects to ConnectServer and GameServer.
* Logs in using username and password.
* Receives and displays the character list.
* Selects a character and enters the game world.
* Parses real-time packets (HP, Mana, SD, AG, Position, Stats, Skills, Server Messages, Objects in Scope, etc.).
* **Basic In-Game Interaction:**
  * Sends step-by-step walk requests (`walk <direction...>`).
  * Sends walk-to-target requests (`walkto <X> <Y>`) — *uses simplified path generation, does not avoid obstacles*.
  * Sends instant move/teleport requests (`move <X> <Y>`).
* Tracks basic character state (HP, Mana, SD, AG, Position, ID, Name).
* Supports different MU Online protocol versions:
  * Season 6 (with support for Extended packets)
  * 0.97d
  * 0.75
* Loads all settings (host, login, version, direction map) from a JSON configuration file.

## Project Structure

```plaintext
MuOnlineConsole/
├── CharacterService.cs         Handles character listing, selection, and movement
├── ConnectionManager.cs        Manages low-level socket connection and encryption
├── LoginService.cs             Handles the login process
├── PacketBuilder.cs            Constructs outgoing network packets
├── PacketRouter.cs             Routes and handles incoming packets
├── Program.cs                  Main application entry point
├── SimpleLoginClient.cs        Core client logic and command processing loop
├── SubCodeHolder.cs            Utility class for handling sub-code packet identification
├── MuOnlineSettings.cs         Configuration model used for appsettings.json
├── appsettings.json            Client configuration (server, login, protocol, direction map)
└── MuOnlineConsole.csproj      Project file
```

![Screenshot](https://i.ibb.co/vvcp7W5w/diagram-2.png)

## Requirements

* **.NET 9 SDK** (or newer)
* A working MU Online server (ConnectServer + GameServer) compatible with one of the supported protocol versions.
* Access to the `MUnique.OpenMU.Network` library (via NuGet).
* (Optional) Access to the `Pipelines.Sockets.Unofficial` library (via NuGet, used by `ConnectionManager`).

## How to Run

1. Clone or download the repository.
2. Open the project in Visual Studio / Rider or use the terminal.
3. **Configure the client:** Open `appsettings.json` and set:
   * `Host`, `Port`: Your server's address and port.
   * `Username`, `Password`: Your login credentials.
   * `ProtocolVersion`: One of `Season6`, `Version097`, `Version075`.
   * `ClientVersion`, `ClientSerial`: Version string and serial used during login.
   * `DirectionMap`: Direction remapping (only if your server uses non-standard mappings).
4. Ensure `appsettings.json` is set to **Copy if newer** in build properties, or add this to your `.csproj`:

   ```xml
   <ItemGroup>
     <None Update="appsettings.json">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
   </ItemGroup>
   ```

5. Restore dependencies, build, and run the project:

   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```

## Usage

When launched, the client will attempt to connect, log in, and select the first available character. Once in the game (`Character is now in-game...`), you can use the following commands:

* `exit`: Closes the client.
* `move X Y`: Sends an instant move (teleport) request to coordinates X, Y.
* `walk <dir1> [dir2] ...`: Sends a step-by-step walk request using the specified directions (0-7). E.g., `walk 6 6 5` (N, N, NE).
* `walkto X Y`: Attempts to generate a simple path (up to 15 steps) towards the target coordinates X, Y and sends a walk request. *Note: Ignores obstacles.*

**Directions (Standard):**  
0:W, 1:SW, 2:S, 3:SE, 4:E, 5:NE, 6:N, 7:NW

## Potential Improvements

* **Character Selection:** Implement interactive character selection from the list.
* **Refactor `PacketRouter`:** Use the Strategy pattern or a Dictionary mapping for better organization.
* **Character State Class:** Encapsulate character state variables into a dedicated class.
* **Full Pathfinding:** Implement an A* algorithm (requires map data) for the `walkto` command to navigate around obstacles.
* **Track Other Objects:** Extend the client state to include information about other players, NPCs, and monsters in scope.
* **More Advanced Actions:** Add support for using skills, NPC interactions, trading, etc.

## License

The source code of this client is provided under the MIT License (or another license if you choose). However, please note that this project utilizes the `MUnique.OpenMU.Network` library, which is subject to its own license. Check the license file of the `MUnique.OpenMU` project before using this client commercially or redistributing it.