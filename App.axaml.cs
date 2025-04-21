using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // Potrzebne dla ILoggerFactory, ILogger, LoggerFactory
using MuOnlineConsole.Configuration;
using MuOnlineConsole.GUI.ViewModels;
using MuOnlineConsole.GUI.Views;

namespace MuOnlineConsole
{
    public partial class App : Application
    {
        public static ILoggerFactory? AppLoggerFactory { get; private set; }
        public static MuOnlineSettings? AppSettings { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory) // Upewnij się, że ścieżka jest poprawna
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                AppLoggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.ClearProviders();
                    builder.AddConfiguration(configuration.GetSection("Logging"));
                    builder.AddSimpleConsole(options =>
                    {
                        configuration.GetSection("Logging:SimpleConsole").Bind(options);
                    });
                    // builder.AddDebug(); // Teraz powinno działać po dodaniu pakietu NuGet
                });

                var mainLogger = AppLoggerFactory.CreateLogger("App");

                AppSettings = configuration.GetSection("MuOnlineSettings").Get<MuOnlineSettings>();
                if (!ValidateSettings(AppSettings, mainLogger))
                {
                    mainLogger.LogError("❌ Configuration validation failed. Please check appsettings.json. Exiting.");
                    // Można tu dodać MessageBox w przyszłości
                    desktop.Shutdown(-1);
                    return;
                }

                mainLogger.LogInformation("✅ Configuration loaded successfully for GUI.");

                // Utwórz ViewModel i Widok
                var viewModel = new MainWindowViewModel(AppLoggerFactory, AppSettings);
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel
                };

                // Inicjalizacja klienta w ViewModelu
                viewModel.InitializeClient();
            }

            base.OnFrameworkInitializationCompleted();
        }

        // Metoda walidacji skopiowana/przeniesiona z Program.cs
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
            // Poprawka: Użyj pełnej ścieżki do enuma
            if (string.IsNullOrWhiteSpace(settings.ProtocolVersion) || !Enum.IsDefined(typeof(MuOnlineConsole.Client.TargetProtocolVersion), settings.ProtocolVersion))
            {
                // Poprawka: Użyj pełnej ścieżki do enuma
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