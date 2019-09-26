using System.Threading.Tasks;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BigBang.Migrator
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            var provider = serviceCollection.BuildServiceProvider();

            var parser = new Parser();
            var parsedArgs = parser.ParseArguments<MigrationOptions>(args);

            await parsedArgs
                .MapResult(async o =>
                {
                    var migrator = provider.GetService<Migrator>();
                    var validated = await migrator.ValidateOptions(o);
                    if (validated)
                    {
                        await migrator.RunMigrations();
                    }
                }, error => Task.FromResult(0));
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(c => c.AddConsole());
            services.AddSingleton<Migrator>();
        }
    }
}
