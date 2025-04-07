using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MuOnlineConsole
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Console.WriteLine("[Info] Attempting to set console codepage to UTF-8 (65001)...");
                    System.Diagnostics.Process.Start("cmd.exe", "/c chcp 65001").WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to set console codepage via chcp: {ex.Message}");
            }

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "HH:mm:ss.fff ";
                    options.SingleLine = true; // Optional: Make logs single line
                })
                       .SetMinimumLevel(LogLevel.Debug)); // Set desired log level

            var settings = configuration.GetSection("MuOnlineSettings").Get<MuOnlineSettings>();
            if (settings == null)
            {
                Console.WriteLine("❌ Failed to load configuration from 'MuOnlineSettings' section.");
                return;
            }

            // Validate essential settings
            if (string.IsNullOrWhiteSpace(settings.ConnectServerHost) || settings.ConnectServerPort == 0)
            {
                Console.WriteLine("❌ Connect Server host or port not configured correctly in appsettings.json.");
                return;
            }
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            {
                Console.WriteLine("❌ Username or password not configured correctly in appsettings.json.");
                return;
            }


            var client = new SimpleLoginClient(loggerFactory, settings);
            await using (client)
            {
                await client.RunAsync();
            }

            Console.WriteLine("Application finished. Press ENTER to exit.");
            Console.ReadLine();
        }
    }
}