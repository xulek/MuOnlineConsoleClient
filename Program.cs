using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MuOnlineConsole.Client;
using MuOnlineConsole.Configuration;

namespace MuOnlineConsole
{
    /// <summary>
    ///  Main entry point of the MuOnlineConsole application.
    ///  Configures logging, loads application settings, validates configuration, and runs the SimpleLoginClient.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        ///  Main method, the application starts execution from here.
        ///  Configures and runs the client, handling setup and teardown.
        /// </summary>
        /// <param name="args">Command line arguments passed to the application.</param>
        /// <returns>A Task representing the asynchronous operation of the application.</returns>
        public static async Task Main(string[] args)
        {
            TrySetConsoleUtf8(); // Attempt to set console output encoding to UTF-8 for better character support

            // Build configuration from appsettings.json
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Load appsettings.json, make it required and reloadable
                .Build();

            // Configure logging using settings from the "Logging" section of the configuration
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.ClearProviders(); // Clear default logging providers to start fresh
                builder.AddConfiguration(configuration.GetSection("Logging")); // Add configuration for logging
                builder.AddSimpleConsole(options => // Add simple console logger
                {
                    configuration.GetSection("Logging:SimpleConsole").Bind(options); // Bind SimpleConsole options from configuration
                });
            }); // LoggerFactory is disposable, ensure proper disposal

            var mainLogger = loggerFactory.CreateLogger("Program"); // Create a logger for the Program class

            // Load MuOnlineSettings from the "MuOnlineSettings" section of the configuration
            var settings = configuration.GetSection("MuOnlineSettings").Get<MuOnlineSettings>();
            if (!ValidateSettings(settings, mainLogger)) // Validate loaded settings
            {
                mainLogger.LogError("❌ Configuration validation failed. Please check appsettings.json.");
                Console.WriteLine("Press ENTER to exit.");
                Console.ReadLine();
                return; // Exit application if settings are invalid
            }

            mainLogger.LogInformation("✅ Configuration loaded successfully.");

            // Create and run the SimpleLoginClient, the core of the application
            var client = new SimpleLoginClient(loggerFactory, settings);
            await using (client) // Ensure client is properly disposed after use
            {
                await client.RunAsync(); // Run the client asynchronously
            }

            Console.WriteLine("Application finished. Press ENTER to exit.");
            Console.ReadLine(); // Keep console window open until key is pressed
        }

        /// <summary>
        /// Attempts to set the console output encoding to UTF-8, primarily for Windows systems, to ensure proper display of Unicode characters.
        /// </summary>
        private static void TrySetConsoleUtf8()
        {
            if (!OperatingSystem.IsWindows()) return; // Only attempt on Windows systems
            try
            {
                Console.WriteLine("[Info] Attempting to set console codepage to UTF-8 (65001)...");
                Console.OutputEncoding = Encoding.UTF8; // Set output encoding to UTF-8
                Console.WriteLine($"[Info] Console OutputEncoding set to {Console.OutputEncoding.EncodingName}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to set console codepage: {ex.Message}"); // Warn if setting encoding fails
            }
        }

        /// <summary>
        /// Validates the MuOnlineSettings to ensure all required configurations are provided and are valid.
        /// Logs errors for each invalid setting found.
        /// </summary>
        /// <param name="settings">The settings object to validate.</param>
        /// <param name="logger">The logger instance to log validation errors.</param>
        /// <returns><c>true</c> if all settings are valid; otherwise, <c>false</c>.</returns>
        private static bool ValidateSettings(MuOnlineSettings? settings, ILogger logger)
        {
            if (settings == null)
            {
                logger.LogError("❌ Failed to load configuration from 'MuOnlineSettings' section.");
                return false; // Configuration section is missing
            }
            bool isValid = true; // Flag to track overall settings validity

            // Check for required settings and log errors if missing or invalid
            if (string.IsNullOrWhiteSpace(settings.ConnectServerHost) || settings.ConnectServerPort == 0)
            {
                logger.LogError("❌ Connect Server host or port not configured correctly.");
                isValid = false;
            }
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            {
                logger.LogError("❌ Username or password not configured correctly.");
                isValid = false;
            }
            if (string.IsNullOrWhiteSpace(settings.ProtocolVersion) || !Enum.IsDefined(typeof(TargetProtocolVersion), settings.ProtocolVersion))
            {
                logger.LogError("❌ ProtocolVersion '{Version}' is invalid or not configured. Valid values: {ValidValues}", settings.ProtocolVersion, string.Join(", ", Enum.GetNames<TargetProtocolVersion>()));
                isValid = false;
            }

            // Log warnings for optional settings that are not configured
            if (string.IsNullOrWhiteSpace(settings.ClientVersion))
            {
                logger.LogWarning("⚠️ ClientVersion is not configured.");
            }
            if (string.IsNullOrWhiteSpace(settings.ClientSerial))
            {
                logger.LogWarning("⚠️ ClientSerial is not configured.");
            }

            return isValid; // Return overall validation status
        }
    }
}