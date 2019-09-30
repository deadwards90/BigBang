using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;

namespace BigBang.Migrator.Models
{
    public class MigratableDatabase
    {
        public string Id { get; set; }
        public List<BigBangContainer> Containers { get; set; }
        public int? Throughput { get; set; }
        public bool UpdateThroughput { get; set; }
    }

    public class BigBangContainer
    {
        public string Id { get; set; }
        public IndexingPolicy IndexingPolicy { get; set; }
        public string PartitionKey { get; set; }
        public UniqueKeyPolicy UniqueKeyPolicy { get; set; }
        public long DefaultTimeToLive { get; set; }
        public int? Throughput { get; set; }
        public List<string> StoredProcedures { get; set; }
        public List<string> UserDefinedFunctions { get; set; }

    }

    public static class IndexingPolicyExtentions
    {
        public static IndexingPolicy CreateDefaultIndexingPolicy()
        {
            return new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent,
                //IncludedPaths = new Collection<IncludedPath>
                //{
                //    new IncludedPath
                //    {
                //        Path = "/*",
                //        Indexes = new Collection<Index>
                //        {
                //            new RangeIndex(DataType.Number, -1),
                //            new RangeIndex(DataType.String, -1),
                //            new SpatialIndex(DataType.Point)
                //        }
                //    }
                //}
            };
        }
    }
}
