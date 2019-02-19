using System.Threading.Tasks;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BigBang.Migrator
{
    class Program
    {
        static void Main(string[] args)
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            var provider = serviceCollection.BuildServiceProvider();

            Parser.Default.ParseArguments<MigrationOptions>(args)
                .WithParsed(async o =>
                {
                    var migrator = provider.GetService<Migrator>();
                    var validated = await migrator.ValidateOptions(o);
                    if (validated)
                    {
                        await migrator.RunMigrations();
                    }
                });
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(c => c.AddConsole());
            services.AddSingleton<Migrator>();
        }
    }
}
