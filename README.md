<p align="center">
  <img width="200" height="200" src="https://github.com/dantheman999301/BigBang/blob/master/BigBang.png?raw=true">
</p>

# BigBang
Cosmos DB Migration Tool written in .NET


## How to run
To use run this query with you connection string and json file:

`dotnet run -- --connection-string "CosmosConnectionString" --file filelocation `

### Example database json file
```
{
  "id": "Databasename",
  "throughput": 400,
  "containers": [
    {
      "id": "table",
      "indexingPolicy": {
        "indexingMode": "consistent",
        "automatic": true,
        "includedPaths": [
          {
            "path": "/*",
            "indexes": [
              {
                "kind": "Range",
                "dataType": "Number",
                "precision": -1
              },
              {
                "kind": "Hash",
                "dataType": "String",
                "precision": 3
              }
            ]
          }
        ]
      },
      "partitionKey": "/partitionKey",
      "defaultTimeToLive": 1000,
      "storedProcedures": [],
      "userDefinedFunctions": []
    }
  ]
}

```