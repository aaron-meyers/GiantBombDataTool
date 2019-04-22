﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace GiantBombDataTool
{
    public sealed class LocalJsonTableStore : ITableStore, ITableStagingStore
    {
        private static readonly JsonSerializerSettings _metadataSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
        };

        private static readonly JsonSerializerSettings _contentSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
        };

        private readonly string _storePath;

        public LocalJsonTableStore(string storePath)
        {
            _storePath = storePath;
        }

        public object Location => _storePath;

        public bool TryInitialize(string resource, Metadata metadata)
        {
            Directory.CreateDirectory(_storePath);

            string metadataPath = GetMetadataPath(resource);

            if (File.Exists(metadataPath) || Directory.Exists(metadataPath))
            {
                Console.WriteLine($"Data store resource already initialized: {metadataPath}");
                return false;
            }

            File.WriteAllText(
                metadataPath,
                JsonConvert.SerializeObject(metadata, _metadataSettings));

            Console.WriteLine($"Initialized data store resource: {metadataPath}");

            return true;
        }

        public bool TryGetMetadata(string resource, out Metadata metadata)
        {
            string path = GetMetadataPath(resource);
            if (!File.Exists(path))
            {
                Console.WriteLine($"Data store resource not found: {path}");
                metadata = null!;
                return false;
            }

            metadata = JsonConvert.DeserializeObject<Metadata>(
                File.ReadAllText(path),
                _metadataSettings);
            return true;
        }

        public void WriteStagedEntities(string resource, IEnumerable<TableEntity> entities)
        {
            var enumerator = entities.GetEnumerator();
            if (!enumerator.MoveNext())
                return;

            long firstId = enumerator.Current.Id;
            string path = GetResourceStagingPath(resource, firstId);

            using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
            {
                do
                {
                    var entity = enumerator.Current;
                    writer.WriteLine(JsonConvert.SerializeObject(entity.Properties, _contentSettings));
                } while (enumerator.MoveNext());
            }
        }

        private string GetMetadataPath(string resource) => Path.Combine(_storePath, $"{resource}.metadata.json");
        private string GetResourceStagingPath(string resource, long id) => Path.Combine(_storePath, $"{resource}.jsonl");
    }
}
