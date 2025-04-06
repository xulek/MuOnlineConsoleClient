using Microsoft.Extensions.Logging;

namespace MuOnlineConsole
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss.fff ")
                       .SetMinimumLevel(LogLevel.Debug));

            var client = new SimpleLoginClient(loggerFactory);
            await using (client)
            {
                await client.RunAsync();
            }

            Console.WriteLine("Application finished. Press ENTER to exit.");
            Console.ReadLine();
        }
    }
}
