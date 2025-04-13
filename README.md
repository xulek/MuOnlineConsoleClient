# MU Online Console Client (.NET Core)

A modern, modular C# client for connecting, logging in, and interacting with a MU Online server. Built upon the `MUnique.OpenMU.Network` library, featuring full packet parsing and protocol version handling.

## Features

* Connects to ConnectServer and GameServer.
* Logs in using username and password.
* Receives and displays the character list.
* Selects a character and enters the game world.
* Parses real-time packets (HP, Mana, SD, AG, Position, Stats, Skills, Server Messages, Objects in Scope, Inventory, Weather, Events, etc.).
* **Basic In-Game Interaction:**
  * Sends step-by-step walk requests (`walk <direction...>`).
  * Sends walk-to-target requests (`walkto <X> <Y>`) — *uses simplified path generation, does not avoid obstacles*.
  * Sends instant move/teleport requests (`move <X> <Y>`).
* Tracks full character state:
  * HP, Mana, SD, AG
  * Current and Max stats
  * Position, Name, ID
  * Inventory (if supported by protocol)
  * Skill list (add/remove updates)
* Supports multiple MU Online protocol versions:
  * Season 6 (with support for Extended packets)
  * 0.97d
  * 0.75
* Automatically switches from ConnectServer to GameServer after server selection.
* Distinguishes packet handling between ConnectServer and GameServer using a unified routing layer.
* Displays messages and chat in real time (including system/golden/guild messages).
* Handles dynamic object scope (players, NPCs, items, zen) and their movement (walked/teleported).
* Loads all settings (host, login, protocol, version, direction map) from a JSON configuration file.

## Project Structure

```plaintext
MuOnlineConsole/
├── Configuration/              ← Configuration models and JSON settings
│   ├── MuOnlineSettings.cs
│   └── appsettings.json
│
├── Core/                       ← Domain logic, core models and utilities
│   ├── Models/
│   │   ├── ScopeObject.cs
│   │   └── ServerInfo.cs
│   └── Utilities/
│       ├── SubCodeHolder.cs
│       └── PacketHandlerAttribute.cs
│
├── Networking/                 ← Network-related functionality
│   ├── PacketHandling/
│   │   ├── PacketRouter.cs
│   │   └── PacketBuilder.cs
│   └── Services/
│       ├── ConnectionManager.cs
│       ├── ConnectServerService.cs
│       ├── LoginService.cs
│       └── CharacterService.cs
│
├── Client/                     ← Main client logic, state handling, commands
│   └── SimpleLoginClient.cs
│
├── Program.cs                  ← Application entry point
├── MuOnlineConsole.csproj      ← Project file
└── README.md
```

## Requirements

* **.NET 9 SDK** (or newer)
* A working MU Online server (ConnectServer + GameServer) compatible with one of the supported protocol versions.
* Access to the `MUnique.OpenMU.Network` library (via NuGet).
* (Optional) Access to the `Pipelines.Sockets.Unofficial` library (via NuGet, used by `ConnectionManager`).

## How to Run

1. Clone or download the repository.
2. Open the project in Visual Studio / Rider or use the terminal.
3. **Configure the client:** Open `appsettings.json` and set:
   * `ConnectServerHost`, `ConnectServerPort`: Your server's address and port.
   * `Username`, `Password`: Your login credentials.
   * `ProtocolVersion`: One of `Season6`, `Version097`, `Version075`.
   * `ClientVersion`, `ClientSerial`: Version string and serial used during login.
   * `DirectionMap`: Direction remapping (only if your server uses non-standard mappings).
4. Ensure `appsettings.json` is set to **Copy if newer** in build properties, or add this to your `.csproj`:

   ```xml
    <ItemGroup>
      <None Update="Configuration\appsettings.json">
        <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        <Link>appsettings.json</Link>
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

When launched, the client will:

* Connect to ConnectServer
* Request and display server list
* Switch to GameServer upon selection
* Authenticate and fetch character list
* Select character and enter the game

### Commands:

* `exit` – Gracefully shuts down the client
* `select <number>` – Selects a character from the list by index
* `scope` – Lists visible in-game objects
* `move X Y` – Sends instant move to coordinates
* `walk <dir1> [dir2] ...` – Sends directional steps (0-7)
* `walkto X Y` – Sends a walk sequence toward target position (approximate)
* `servers` / `list` – Displays available servers (from ConnectServer)
* `connect <id>` – Connects to a selected server from the list

**Directions (Standard):**  
0:W, 1:SW, 2:S, 3:SE, 4:E, 5:NE, 6:N, 7:NW

## Potential Improvements

* Full A* pathfinding (obstacle-aware `walkto`)
* Extended support for NPC interaction, trading, skill usage
* GUI frontend (e.g., Avalonia, WinForms)
* Packet recording and replay
* Full character management (create/delete)
* Auto-reconnect and ping monitoring

## License

This project is released under the MIT License.

Note: The client uses the `MUnique.OpenMU.Network` library which has its own licensing. Check the original repository for full license terms if reusing the networking components in another project.