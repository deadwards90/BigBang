using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using BigBang.Migrator.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BigBang.Migrator
{
    public class Migrator
    {
        private readonly ILogger<Migrator> _logger;
        private CosmosClient _client;
        private string _baseFilePath;

        public Migrator(ILogger<Migrator> logger)
        {
            _logger = logger;
        }

        public async Task<bool> ValidateOptions(MigrationOptions options)
        {
            _client = new CosmosClient(options.ConnectionString);
            try
            {
                _logger.LogInformation("Getting account settings to check connection");
                await _client.GetAccountSettingsAsync();
                _logger.LogInformation("Account settings retrieved");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to get account settings, please check connection string");
                return false;
            }

            if (!File.Exists(options.FileLocation))
            {
                _logger.LogError("Cannot find JSON File, please check it exists and has been entered correctly");
                return false;
            }

            _baseFilePath = options.FileLocation;
            return true;
        }

        public async Task RunMigrations()
        {
            var requestedDatabase = JsonConvert.DeserializeObject<MigratableDatabase>(File.ReadAllText(_baseFilePath));

            var directoryFullPath = new FileInfo(_baseFilePath).Directory.FullName;

            _logger.LogInformation("Starting migrations, checking database");

            var cloudDatabase = await CreateOrGetDatabase(_client, requestedDatabase);

            var cloudContainers = (await cloudDatabase.Containers.GetContainerIterator().FetchNextSetAsync()).ToList();

            _logger.LogInformation("Checking current containers");

            var (containersToCreate, containersToUpdate, containersToDelete) = SplitContainers(requestedDatabase, cloudContainers);

            _logger.LogInformation($"Creating {containersToCreate.Count} containers");

            await CreateContainers(containersToCreate, cloudDatabase, directoryFullPath);

            _logger.LogInformation($"Updating {containersToUpdate.Count} containers");

            await UpdateContainers(containersToUpdate, cloudDatabase, directoryFullPath);

            _logger.LogInformation($"Deleting {containersToDelete.Count} containers");

            await DeleteContainers(containersToDelete, cloudDatabase);

            _logger.LogInformation("Finished");
        }

        private static (IList<Container> containersToCreate, IList<Container> containersToUpdate, IList<Container>
            containersToDelete) SplitContainers(
                MigratableDatabase requestedDatabase, IList<CosmosContainerSettings> cloudContainers)
        {
            var containersToCreate = requestedDatabase.Containers
                .Where(c => cloudContainers.All(cc => cc.Id != c.Id))
                .ToList();

            var containersToUpdate = requestedDatabase.Containers
                .Where(c => cloudContainers.Any(cc => cc.Id == c.Id))
                .ToList();

            var containersToDelete = requestedDatabase.Containers
                .Except(containersToUpdate)
                .Except(containersToCreate)
                .ToList();
            return (containersToCreate, containersToUpdate, containersToDelete);
        }

        private async Task<CosmosDatabase> CreateOrGetDatabase(CosmosClient client, MigratableDatabase requestedDatabase)
        {
            var response =
                await client.Databases.CreateDatabaseIfNotExistsAsync(requestedDatabase.Id, requestedDatabase.Throughput);
            var cloudDatabase = response.Database;

            if (response.StatusCode == HttpStatusCode.Conflict && requestedDatabase.Throughput.HasValue)
            {
                _logger.LogInformation("Database already exists, replacing throughput");
                await cloudDatabase.ReplaceProvisionedThroughputAsync(requestedDatabase.Throughput.Value);
            }
            else
            {
                _logger.LogInformation("Created new database");
            }

            return cloudDatabase;
        }

        private async Task CreateContainers(IEnumerable<Container> containersToCreate, CosmosDatabase cloudDatabase,
            string directoryFullPath)
        {
            foreach (var container in containersToCreate)
            {
                var cosmosContainerSettings = new CosmosContainerSettings(container.Id, container.PartitionKey)
                {
                    DefaultTimeToLive = TimeSpan.FromSeconds(container.DefaultTimeToLive),
                    UniqueKeyPolicy = container.UniqueKeyPolicy ?? new UniqueKeyPolicy()
                };

                if (container.IndexingPolicy != null)
                    cosmosContainerSettings.IndexingPolicy = container.IndexingPolicy;

                _logger.LogInformation($"Creating container {container.Id}");

                var containerResponse = await cloudDatabase.Containers.CreateContainerAsync(cosmosContainerSettings);

                var cloudContainer = containerResponse.Container;

                foreach (var file in container.StoredProcedures)
                {
                    _logger.LogInformation($"Creating stored procedure {file}");

                    var id = Path.GetFileNameWithoutExtension(Path.Combine(directoryFullPath, file));
                    await cloudContainer.StoredProcedures.CreateStoredProceducreAsync(id,
                        await File.ReadAllTextAsync(Path.Combine(directoryFullPath, file)));
                }


                // TODO: Implement UDFs when available
                //foreach (var file in container.UserDefinedFunctions)
                //{
                //    var id = Path.GetFileNameWithoutExtension(file);
                //}
            }
        }

        private async Task UpdateContainers(IEnumerable<Container> containersToUpdate, CosmosDatabase cloudDatabase,
            string directoryFullPath)
        {
            foreach (var container in containersToUpdate)
            {
                var containerToUpdate = cloudDatabase.Containers[container.Id];

                if (container.Throughput.HasValue)
                    await containerToUpdate.ReplaceProvisionedThroughputAsync(container.Throughput.Value);

                var cosmosContainerSettings = new CosmosContainerSettings(containerToUpdate.Id, container.PartitionKey)
                {
                    DefaultTimeToLive = TimeSpan.FromSeconds(container.DefaultTimeToLive),
                    UniqueKeyPolicy = container.UniqueKeyPolicy ?? new UniqueKeyPolicy(),
                    IndexingPolicy = container.IndexingPolicy ??
                                     IndexingPolicyExtentions.CreateDefaultIndexingPolicy()
                };

                _logger.LogInformation($"Updating container {container.Id}");

                await containerToUpdate.ReplaceAsync(cosmosContainerSettings);

                _logger.LogInformation("Checking which stored procedures currently exist");

                var currentStoredProcedures = (await containerToUpdate.StoredProcedures
                    .GetStoredProcedureIterator()
                    .FetchNextSetAsync())
                    .Select(c => c.Id)
                    .ToList();

                foreach (var file in container.StoredProcedures)
                {
                    var id = file.Replace(".js", "");
                    if (currentStoredProcedures.Contains(id))
                    {
                        _logger.LogInformation($"Replacing stored procedure {file}");

                        var storedProc = containerToUpdate.StoredProcedures[id];
                        await storedProc.ReplaceAsync(await File.ReadAllTextAsync(Path.Combine(directoryFullPath, file)));
                    }
                    else
                    {
                        _logger.LogInformation($"Creating stored procedure {file}");

                        await containerToUpdate.StoredProcedures.CreateStoredProceducreAsync(id,
                            await File.ReadAllTextAsync(Path.Combine(directoryFullPath, file)));
                    }
                }

                foreach (var id in currentStoredProcedures.Except(container.StoredProcedures))
                {
                    _logger.LogInformation($"Deleting stored procedure {id}");
                    await containerToUpdate.StoredProcedures[id].DeleteAsync();
                }
            }
        }

        private async Task DeleteContainers(IEnumerable<Container> containersToDelete, CosmosDatabase cloudDatabase)
        {
            foreach (var container in containersToDelete)
            {
                var containerToDelete = cloudDatabase.Containers[container.Id];

                _logger.LogInformation($"Deleting container {container.Id}");

                await containerToDelete.DeleteAsync();
            }
        }
    }
}
