using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MuOnlineConsole.Configuration;
using MuOnlineConsole.GUI.ViewModels;
using MuOnlineConsole.GUI.Views;
using System;
using System.Linq;

namespace MuOnlineConsole
{
    /// <summary>
    /// The main application class for the Avalonia UI.
    /// </summary>
    public partial class App : Application
    {
        // Static accessors for LoggerFactory and Settings (consider alternatives like Dependency Injection in larger apps)
        public static ILoggerFactory? AppLoggerFactory { get; private set; }
        public static MuOnlineSettings? AppSettings { get; private set; }

        /// <summary>
        /// Initializes the Avalonia application framework.
        /// </summary>
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Called when the Avalonia framework initialization is complete.
        /// Sets up logging, configuration, creates the main window, and initializes the client.
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Build configuration from appsettings.json
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                // Configure logging
                AppLoggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.ClearProviders();
                    builder.AddConfiguration(configuration.GetSection("Logging"));
                    builder.AddSimpleConsole(options =>
                    {
                        configuration.GetSection("Logging:SimpleConsole").Bind(options);
                    });
                    // Add other loggers like Debug or File if needed and configured
                });

                var mainLogger = AppLoggerFactory.CreateLogger("App");

                // Load application settings
                AppSettings = configuration.GetSection("MuOnlineSettings").Get<MuOnlineSettings>();

                // Validate settings and shutdown if invalid
                if (!ValidateSettings(AppSettings, mainLogger))
                {
                    mainLogger.LogError("❌ Configuration validation failed. Please check appsettings.json. Exiting.");
                    desktop.Shutdown(-1); // Shutdown with error code
                    return;
                }

                mainLogger.LogInformation("✅ Configuration loaded successfully for GUI.");

                // Create the main window and view model
                var viewModel = new MainWindowViewModel(AppLoggerFactory, AppSettings);
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel
                };

                // Initialize the client within the view model
                viewModel.InitializeClient();
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Validates the loaded application settings.
        /// </summary>
        /// <param name="settings">The application settings object.</param>
        /// <param name="logger">The logger for reporting validation errors.</param>
        /// <returns>True if settings are valid, false otherwise.</returns>
        private static bool ValidateSettings(MuOnlineSettings? settings, ILogger logger)
        {
            if (settings == null)
            {
                logger.LogError("❌ Failed to load configuration from 'MuOnlineSettings' section.");
                return false;
            }
            bool isValid = true;

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

            // Validate ProtocolVersion against the enum
            if (string.IsNullOrWhiteSpace(settings.ProtocolVersion) || !Enum.TryParse<MuOnlineConsole.Client.TargetProtocolVersion>(settings.ProtocolVersion, out _))
            {
                logger.LogError("❌ ProtocolVersion '{Version}' is invalid or not configured. Valid values: {ValidValues}", settings.ProtocolVersion, string.Join(", ", Enum.GetNames<MuOnlineConsole.Client.TargetProtocolVersion>()));
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(settings.ClientVersion))
            {
                logger.LogWarning("⚠️ ClientVersion is not configured.");
            }
            if (string.IsNullOrWhiteSpace(settings.ClientSerial))
            {
                logger.LogWarning("⚠️ ClientSerial is not configured.");
            }

            return isValid;
        }
    }
}