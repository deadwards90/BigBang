using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BigBang.Migrator
{
    public static class FeedIteratorExtensions
    {
        public static async Task<IEnumerable<T>> GetAll<T>(this FeedIterator<T> feedIterator)
        {
            var returnList = new List<T>();
            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync();
                returnList.AddRange(response);
            }

            return returnList;
        }
    }
}
