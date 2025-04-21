using Avalonia;
using System;
using System.Text;

namespace MuOnlineConsole
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread] // Wymagane dla aplikacji desktopowych
        public static void Main(string[] args)
        {
            TrySetConsoleUtf8(); // Nadal może być przydatne dla logów konsolowych

            try
            {
                BuildAvaloniaApp() // Zbuduj aplikację Avalonia
                    .StartWithClassicDesktopLifetime(args); // Uruchom ją
            }
            catch (Exception ex)
            {
                // Log critical error if Avalonia fails to start
                Console.WriteLine($"[CRITICAL] Failed to start Avalonia application: {ex}");
                // Możesz tu dodać logowanie do pliku
            }
            finally
            {
                Console.WriteLine("Application finished. Press ENTER to exit.");
                Console.ReadLine(); // Pozostaw konsolę otwartą na koniec
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // .WithInterFont() // Użyj czcionki Inter
                .LogToTrace(); // Loguj błędy Avalonia

        // Metoda TrySetConsoleUtf8 pozostaje bez zmian
        private static void TrySetConsoleUtf8()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                Console.WriteLine("[Info] Attempting to set console codepage to UTF-8 (65001)...");
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine($"[Info] Console OutputEncoding set to {Console.OutputEncoding.EncodingName}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to set console codepage: {ex.Message}");
            }
        }
    }
}