using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using BigBang.Migrator.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
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
                await _client.ReadAccountAsync();
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
            
            var cloudContainers = (await cloudDatabase.GetContainerQueryIterator<ContainerProperties>().GetAll()).ToList();

            _logger.LogInformation("Checking current containers");

            var (containersToCreate, containersToUpdate, containersToDelete) = SplitContainers(requestedDatabase, cloudContainers);

            _logger.LogInformation($"Creating {containersToCreate.Count} containers");

            await CreateContainers(containersToCreate, cloudDatabase, directoryFullPath);

            _logger.LogInformation($"Updating {containersToUpdate.Count} containers");

            await UpdateContainers(containersToUpdate, cloudDatabase, requestedDatabase.UpdateThroughput, directoryFullPath);

            _logger.LogInformation($"Deleting {containersToDelete.Count} containers");

            await DeleteContainers(containersToDelete, cloudDatabase);

            _logger.LogInformation("Finished");
        }

        private static (IList<BigBangContainer> containersToCreate, 
            IList<BigBangContainer> containersToUpdate, 
            IList<ContainerProperties> containersToDelete) SplitContainers (
                MigratableDatabase requestedDatabase, IList<ContainerProperties> cloudContainers)
        {
            var containersToCreate = requestedDatabase.Containers
                .Where(c => cloudContainers.All(cc => cc.Id != c.Id))
                .ToList();

            var containersToUpdate = requestedDatabase.Containers
                .Where(c => cloudContainers.Any(cc => cc.Id == c.Id))
                .ToList();

            var containersToDelete = cloudContainers
                .Where(c => containersToCreate.All(cc => cc.Id != c.Id))
                .Where(c => containersToUpdate.All(cc => cc.Id != c.Id))
                .ToList();

            return (containersToCreate, containersToUpdate, containersToDelete);
        }

        private async Task<Database> CreateOrGetDatabase(CosmosClient client, MigratableDatabase requestedDatabase)
        {
            var response =
                await client.CreateDatabaseIfNotExistsAsync(requestedDatabase.Id, requestedDatabase.Throughput);
            var cloudDatabase = response.Database;

            if (response.StatusCode != HttpStatusCode.Created && requestedDatabase.Throughput.HasValue)
            {
                _logger.LogInformation("Database already exists, replacing throughput");
                await cloudDatabase.ReplaceThroughputAsync(requestedDatabase.Throughput.Value);
            }
            else
            {
                _logger.LogInformation("Created new database");
            }

            return cloudDatabase;
        }

        private async Task CreateContainers(IEnumerable<BigBangContainer> containersToCreate, Database cloudDatabase,
            string directoryFullPath)
        {
            foreach (var container in containersToCreate)
            {
                var cosmosContainerSettings = new ContainerProperties(container.Id, container.PartitionKey)
                {
                    DefaultTimeToLive = Convert.ToInt32(TimeSpan.FromSeconds(container.DefaultTimeToLive).TotalSeconds),
                    UniqueKeyPolicy = container.UniqueKeyPolicy ?? new UniqueKeyPolicy()
                };

                if (container.IndexingPolicy != null)
                    cosmosContainerSettings.IndexingPolicy = container.IndexingPolicy;

                _logger.LogInformation($"Creating container {container.Id}");

                var containerResponse = await cloudDatabase.CreateContainerAsync(cosmosContainerSettings);

                var cloudContainer = containerResponse.Container;

                foreach (var file in container.StoredProcedures)
                {
                    _logger.LogInformation($"Creating stored procedure {file}");
                    await cloudContainer.Scripts.CreateStoredProcedureAsync(await GetStoredProcedurePropertiesFromPath(directoryFullPath, file));
                }

                foreach (var file in container.UserDefinedFunctions)
                {
                    _logger.LogInformation($"Creating user defined function {file}");
                    await cloudContainer.Scripts.CreateUserDefinedFunctionAsync(await GetUserDefinedFunctionPropertiesFromPath(directoryFullPath, file));
                }
            }
        }

        private async Task UpdateContainers(IEnumerable<BigBangContainer> containersToUpdate, 
            Database cloudDatabase,
            bool updateThroughput,
            string directoryFullPath)
        {
            foreach (var container in containersToUpdate)
            {
                var containerToUpdate = cloudDatabase.GetContainer(container.Id);

                if (updateThroughput && container.Throughput.HasValue)
                    await containerToUpdate.ReplaceThroughputAsync(container.Throughput.Value);

                var cosmosContainerSettings = new ContainerProperties(containerToUpdate.Id, container.PartitionKey)
                {
                    DefaultTimeToLive = Convert.ToInt32(TimeSpan.FromSeconds(container.DefaultTimeToLive).TotalSeconds),
                    UniqueKeyPolicy = container.UniqueKeyPolicy ?? new UniqueKeyPolicy(),
                    IndexingPolicy = container.IndexingPolicy ??
                                     IndexingPolicyExtentions.CreateDefaultIndexingPolicy()
                };

                _logger.LogInformation($"Updating container {container.Id}");

                await containerToUpdate.ReplaceContainerAsync(cosmosContainerSettings);

                _logger.LogInformation("Checking which stored procedures currently exist");

                var currentStoredProcedures = (await containerToUpdate.Scripts
                    .GetStoredProcedureQueryIterator<dynamic>()
                    .GetAll())
                    .Select(c => c.Id)
                    .ToList();

                foreach (var file in container.StoredProcedures)
                {
                    var id = file.Replace(".js", "");
                    if (currentStoredProcedures.Contains(id))
                    {
                        _logger.LogInformation($"Replacing stored procedure {file}");
                        await containerToUpdate.Scripts.ReplaceStoredProcedureAsync(await GetStoredProcedurePropertiesFromPath(directoryFullPath, file));
                    }
                    else
                    {
                        _logger.LogInformation($"Creating stored procedure {file}");
                        await containerToUpdate.Scripts.CreateStoredProcedureAsync(await GetStoredProcedurePropertiesFromPath(directoryFullPath, file));
                    }
                }

                foreach (var id in currentStoredProcedures.Except(container.StoredProcedures))
                {
                    _logger.LogInformation($"Deleting stored procedure {id}");
                    await containerToUpdate.Scripts.DeleteStoredProcedureAsync(id);
                }

                var currentUserDefinedFuncs = (await containerToUpdate.Scripts
                        .GetUserDefinedFunctionQueryIterator<dynamic>()
                        .GetAll())
                        .Select(c => c.Id)
                        .ToList();

                foreach (var file in container.UserDefinedFunctions)
                {
                    var id = file.Replace(".js", "");
                    if (currentUserDefinedFuncs.Contains(id))
                    {
                        _logger.LogInformation($"Replacing user defined function {file}");
                        await containerToUpdate.Scripts.ReplaceUserDefinedFunctionAsync(await GetUserDefinedFunctionPropertiesFromPath(directoryFullPath, file));
                    }
                    else
                    {
                        _logger.LogInformation($"Creating user defined function  {file}");
                        await containerToUpdate.Scripts.CreateUserDefinedFunctionAsync(await GetUserDefinedFunctionPropertiesFromPath(directoryFullPath, file));
                    }
                }

                foreach (var id in currentUserDefinedFuncs.Except(container.UserDefinedFunctions))
                {
                    _logger.LogInformation($"Deleting user defined function {id}");
                    await containerToUpdate.Scripts.DeleteUserDefinedFunctionAsync(id);
                }
            }
        }

        private async Task DeleteContainers(IEnumerable<ContainerProperties> containersToDelete, Database cloudDatabase)
        {
            foreach (var container in containersToDelete)
            {
                var containerToDelete = cloudDatabase.GetContainer(container.Id);

                _logger.LogInformation($"Deleting container {container.Id}");

                await containerToDelete.DeleteContainerAsync();
            }
        }

        private async Task<StoredProcedureProperties> GetStoredProcedurePropertiesFromPath(string directoryFullPath, string filePath)
        {
            var id = Path.GetFileNameWithoutExtension(Path.Combine(directoryFullPath, filePath));

            return new StoredProcedureProperties
            {
                Id = id,
                Body = await File.ReadAllTextAsync(Path.Combine(directoryFullPath, filePath))
            };
        }

        private async Task<UserDefinedFunctionProperties> GetUserDefinedFunctionPropertiesFromPath(string directoryFullPath, string filePath)
        {
            var id = Path.GetFileNameWithoutExtension(Path.Combine(directoryFullPath, filePath));

            return new UserDefinedFunctionProperties
            {
                Id = id,
                Body = await File.ReadAllTextAsync(Path.Combine(directoryFullPath, filePath))
            };
        }

    }
}
