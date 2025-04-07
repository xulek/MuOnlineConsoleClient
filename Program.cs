using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MuOnlineConsole
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss.fff ")
                       .SetMinimumLevel(LogLevel.Debug));

            var settings = configuration.GetSection("MuOnlineSettings").Get<MuOnlineSettings>();
            if (settings == null)
            {
                Console.WriteLine("❌ Failed to load configuration from 'MuOnlineSettings' section.");
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
