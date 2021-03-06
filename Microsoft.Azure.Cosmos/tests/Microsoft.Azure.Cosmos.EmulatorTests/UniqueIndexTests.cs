﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.SDK.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Runtime.ExceptionServices;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [TestClass]
    [TestCategory("Quarantine") /* Used to filter out quarantined tests in gated runs */]
    public class UniqueIndexTests
    {
        private DocumentClient client;  // This is only used for housekeeping this.database.
        private CosmosDatabaseSettings database;

        [TestInitialize]
        public void TestInitialize()
        {
            this.client = TestCommon.CreateClient(true);
            this.database = TestCommon.CreateOrGetDatabase(this.client);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            this.client.DeleteDatabaseAsync(this.database).Wait();
        }

        [TestMethod]
        public void InsertWithUniqueIndex()
        {
            var collectionSpec = new CosmosContainerSettings {
                Id = "InsertWithUniqueIndexConstraint_" + Guid.NewGuid(),
                UniqueKeyPolicy = new UniqueKeyPolicy {
                    UniqueKeys = new Collection<UniqueKey> {
                        new UniqueKey {
                            Paths = new Collection<string> { "/name", "/address" }
                        }
                    }
                },
                IndexingPolicy = new IndexingPolicy {
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths = new Collection<IncludedPath> { 
                        new IncludedPath { Path = "/name/?", Indexes = new Collection<Index> { new HashIndex(DataType.String, 7) } },
                        new IncludedPath { Path = "/address/?", Indexes = new Collection<Index> { new HashIndex(DataType.String, 7) } },
                    },
                    ExcludedPaths = new Collection<ExcludedPath> { 
                        new ExcludedPath { Path = "/*" }
                    }
                }
            };

            Func<DocumentClient, CosmosContainerSettings, Task> testFunction = async (DocumentClient client, CosmosContainerSettings collection) =>
            {
                var doc1 = JObject.Parse("{\"name\":\"Alexander Pushkin\",\"address\":\"Russia 630090\"}");
                var doc2 = JObject.Parse("{\"name\":\"Alexander Pushkin\",\"address\":\"Russia 640000\"}");
                var doc3 = JObject.Parse("{\"name\":\"Mihkail Lermontov\",\"address\":\"Russia 630090\"}");

                await client.CreateDocumentAsync(collection, doc1);

                try
                {
                    await client.CreateDocumentAsync(collection, doc1);
                    Assert.Fail("Did not throw due to unique constraint (create)");
                }
                catch (DocumentClientException ex)
                {
                    Assert.AreEqual(StatusCodes.Conflict, (StatusCodes)ex.StatusCode);
                }

                try
                {
                    await client.UpsertDocumentAsync(collection.SelfLink, doc1);
                    Assert.Fail("Did not throw due to unique constraint (upsert)");
                }
                catch (DocumentClientException ex)
                {
                    // TODO: unique index: there seems to be a backend bug. For unique index violation, upsert should return Conflict rather than RetryWith.
                    // Search for: L"For upsert insert, if it failed with E_RESOURCE_ALREADY_EXISTS, return E_CONCURRENCY_VIOLATION so that client will retry"
                    Assert.AreEqual(StatusCodes.RetryWith, (StatusCodes)ex.StatusCode);
                }

                await client.CreateDocumentAsync(collection, doc2);
                await client.CreateDocumentAsync(collection, doc3);
            };

            TestForEachClient(collectionSpec, testFunction, "InsertWithUniqueIndex");
        }

        [TestMethod]
        public void ReplaceAndDeleteWithUniqueIndex()
        {
            var collectionSpec = new CosmosContainerSettings
            {
                Id = "InsertWithUniqueIndexConstraint_" + Guid.NewGuid(),
                UniqueKeyPolicy = new UniqueKeyPolicy
                {
                    UniqueKeys = new Collection<UniqueKey> { new UniqueKey { Paths = new Collection<string> { "/name", "/address" } } }
                }
            };

            Func<DocumentClient, CosmosContainerSettings, Task> testFunction = async (DocumentClient client, CosmosContainerSettings collection) =>
            {
                var doc1 = JObject.Parse("{\"name\":\"Alexander Pushkin\",\"address\":\"Russia 630090\"}");
                var doc2 = JObject.Parse("{\"name\":\"Mihkail Lermontov\",\"address\":\"Russia 630090\"}");
                var doc3 = JObject.Parse("{\"name\":\"Alexander Pushkin\",\"address\":\"Russia 640000\"}");

                Document doc1Inserted = await client.CreateDocumentAsync(collection, doc1);

                await client.ReplaceDocumentAsync(doc1Inserted.SelfLink, doc1Inserted);     // Replace with same values -- OK.

                Document doc2Inserted = await client.CreateDocumentAsync(collection, doc2);
                var doc2Replacement = JObject.Parse(JsonConvert.SerializeObject(doc1Inserted));
                doc2Replacement["id"] = doc2Inserted.Id;

                try
                {
                    await client.ReplaceDocumentAsync(doc2Inserted.SelfLink, doc2Replacement); // Replace doc2 with values from doc1 -- Conflict.
                    Assert.Fail("Did not throw due to unique constraint");
                }
                catch (DocumentClientException ex)
                {
                    Assert.AreEqual(StatusCodes.Conflict, (StatusCodes)ex.StatusCode);
                }

                doc3["id"] = doc1Inserted.Id;
                await client.ReplaceDocumentAsync(doc1Inserted.SelfLink, doc3);             // Replace with values from doc3 -- OK.

                await client.DeleteDocumentAsync(doc1Inserted.SelfLink);
                await client.CreateDocumentAsync(collection, doc1);
            };

            TestForEachClient(collectionSpec, testFunction, "ReplaceAndDeleteWithUniqueIndex");
        }

        [TestMethod]
        [Description("Make sure that the pair (PK, unique key) is globally (not depending on partion/PK) unique")]
        public void TestGloballyUniquenessOfFieldAndPartitionedKeyPair()
        {
            using (DocumentClient client = TestCommon.CreateClient(true, tokenType: AuthorizationTokenType.PrimaryMasterKey))
            {
                TestGloballyUniqueFieldForPartitionedCollectionHelperAsync(client).Wait();
            }
        }

        private async Task TestGloballyUniqueFieldForPartitionedCollectionHelperAsync(DocumentClient client)
        {
            var collectionSpec = new CosmosContainerSettings
            {
                Id = "TestGloballyUniqueFieldForPartitionedCollection_" + Guid.NewGuid(),
                PartitionKey = new PartitionKeyDefinition
                {
                    Kind = PartitionKind.Hash,
                    Paths = new Collection<string> { "/pk" }
                },
                UniqueKeyPolicy = new UniqueKeyPolicy
                {
                    UniqueKeys = new Collection<UniqueKey> {
                        new UniqueKey { Paths = new Collection<string> { "/name" } }
                    }
                },
                IndexingPolicy = new IndexingPolicy
                {
                    IndexingMode = IndexingMode.Consistent,
                    IncludedPaths = new Collection<IncludedPath> { 
                        new IncludedPath { Path = "/name/?", Indexes = new Collection<Index> { new HashIndex(DataType.String, 7) } },
                    },
                    ExcludedPaths = new Collection<ExcludedPath> { 
                        new ExcludedPath { Path = "/*" }
                    }
                }
            };

            var collection = await client.CreateDocumentCollectionAsync(
                this.database, 
                collectionSpec, 
                new RequestOptions { OfferThroughput = 20000 });

            const int partitionCount = 50;
            var partitionKeyValues = new List<string>();
            for (int i = 0; i < partitionCount * 3; ++i)
            {
                partitionKeyValues.Add(Guid.NewGuid().ToString());
            }

            string documentTemplate = "{{ \"pk\":\"{0}\", \"name\":\"{1}\" }}";
            foreach (string partitionKey in partitionKeyValues)
            {
                string document = string.Format(documentTemplate, partitionKey, "Same Name");
                await client.CreateDocumentAsync(collection, JObject.Parse(document));
            }

            string conflictDocument = string.Format(documentTemplate, partitionKeyValues[0], "Same Name");
            try
            {
                await client.CreateDocumentAsync(collection, JObject.Parse(conflictDocument));
                Assert.Fail("Did not throw due to unique constraint");
            }
            catch (DocumentClientException ex)
            {
                Assert.AreEqual(StatusCodes.Conflict, (StatusCodes)ex.StatusCode);
            }
        }

        private void TestForEachClient(CosmosContainerSettings collectionSpec, Func<DocumentClient, CosmosContainerSettings, Task> testFunction, string scenarioName)
        {
            Func<DocumentClient, DocumentClientType, Task<int>> wrapperFunction = async (DocumentClient client, DocumentClientType clientType) =>
            {
                var collection = await client.CreateDocumentCollectionAsync(this.database, collectionSpec);

                // Normally we would delete collection in in finally block, but can't await there.
                // Delete collection is needed so that next client from Util.TestForEachClient starts fresh.
                ExceptionDispatchInfo dispatchInfo = null;
                try
                {
                    await testFunction(client, collection);
                }
                catch (Exception ex)
                {
                    dispatchInfo = ExceptionDispatchInfo.Capture(ex);
                }

                try
                {
                    await client.DeleteDocumentCollectionAsync(collection);
                }
                finally
                {
                    if (dispatchInfo != null)
                    {
                        dispatchInfo.Throw();
                    }
                }

                return 0;
            };

            Util.TestForEachClient(wrapperFunction, scenarioName);
        }
    }
}
