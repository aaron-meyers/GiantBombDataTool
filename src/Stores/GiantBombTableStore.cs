﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Talys.Stores
{
    public sealed class GiantBombTableStore : IReadOnlyTableStore
    {
        private readonly WebClient _webClient;
        private readonly bool _verbose;
        private string? _apiKey;
        private DateTime _nextRequest = DateTime.MinValue;

        public GiantBombTableStore(string userAgent, string? apiKey = null, bool verbose = false)
        {
            _webClient = new InternalWebClient(userAgent);
            _apiKey = apiKey;
            _verbose = verbose;
        }

        public IEnumerable<TableEntity> GetEntitiesByTimestamp(
            string table,
            TableConfig config,
            DateTime? lastTimestamp,
            IEnumerable<long> initialLastIds)
        {
            SetApiKeyIfNeeded(config);

            // TODO: support persisting lastIds
            var lastIds = new HashSet<long>(initialLastIds);

            bool finished = false;

            while (!finished)
            {
                string lastUpdateText = lastTimestamp != null ?
                    lastTimestamp.Value.ToString("yyyy-MM-dd HH:mm:ss") :
                    "forever";
                Console.WriteLine($"Retrieving {table} updated since {lastUpdateText}");

                var uri = BuildListUri(
                    table,
                    string.Join(",", config.Fields),
                    limit: 100,
                    sort: "date_last_updated:asc",
                    dateLastUpdated: lastTimestamp);
                var result = Download(uri);

                finished = result["number_of_page_results"].Value<long>() ==
                    result["number_of_total_results"].Value<long>();

                DateTime? firstTimestamp = null;

                foreach (var item in result["results"])
                {
                    var (id, timestamp) = ParseEntityProperties(item);

                    if (timestamp == lastTimestamp && lastIds.Contains(id))
                        continue; // don't add the item again

                    if (firstTimestamp == null)
                        firstTimestamp = timestamp;

                    if (lastTimestamp != timestamp)
                        lastIds.Clear();

                    lastIds.Add(id);
                    lastTimestamp = timestamp;

                    yield return new TableEntity(id, timestamp, (JObject)item);
                }

                long offset = 0;
                while (firstTimestamp == lastTimestamp)
                {
                    // TODO: figure out how to avoid duplicating code from above

                    offset += 99;

                    uri = BuildListUri(
                        table,
                        string.Join(",", config.Fields),
                        offset,
                        limit: 100,
                        sort: "date_last_updated:asc",
                        dateLastUpdated: lastTimestamp);
                    result = Download(uri);

                    finished = result["number_of_page_results"].Value<long>() ==
                        result["number_of_total_results"].Value<long>();

                    firstTimestamp = null;

                    foreach (var item in result["results"])
                    {
                        var (id, timestamp) = ParseEntityProperties(item);

                        if (timestamp == lastTimestamp && lastIds.Contains(id))
                        {
                            if (firstTimestamp == null)
                                firstTimestamp = timestamp;

                            continue; // don't add the item again
                        }
                        else if (firstTimestamp == null)
                        {
                            Console.WriteLine($"Expecting one of {string.Join(",", lastIds)} but found {id}");
                            yield break;
                        }

                        if (lastTimestamp != timestamp)
                            lastIds.Clear();

                        lastIds.Add(id);
                        lastTimestamp = timestamp;

                        yield return new TableEntity(id, timestamp, (JObject)item);
                    }
                }
            }
        }

        public TableEntity GetEntityDetail(string table, TableConfig config, long id)
        {
            string url = $"https://www.giantbomb.com/api/{config.DetailName ?? table.Substring(0, table.Length - 1)}/{config.Id}-{id}/";
            return GetEntityDetailCore(table, config, url);
        }

        public TableEntity GetEntityDetail(string table, TableConfig config, TableEntity entity)
        {
            string url = entity.Content["api_detail_url"].Value<string>();
            return GetEntityDetailCore(table, config, url);
        }

        private TableEntity GetEntityDetailCore(string table, TableConfig config, string url)
        {
            SetApiKeyIfNeeded(config);

            Console.WriteLine($"Retrieving {url}");

            var fullUri = BuildDetailUri(
                url,
                string.Join(",", config.Fields.Concat(config.DetailFields)));
            var result = Download(fullUri);
            var detail = (JObject)result["results"];

            var (id, timestamp) = ParseEntityProperties(detail);

            return new TableEntity(id, timestamp, detail);
        }

        private void SetApiKeyIfNeeded(TableConfig config)
        {
            if (string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(config.ApiKey))
                _apiKey = config.ApiKey;
        }

        private static (long Id, DateTime Timestamp) ParseEntityProperties(JToken item)
        {
            long id = item["id"].Value<long>();
            var timestamp = DateTime.Parse(
                item["date_last_updated"].Value<string>().Replace(' ', 'T') + 'Z',
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal);
            return (id, timestamp);
        }

        private Uri BuildListUri(
            string resource,
            string fields,
            long offset = 0,
            long? limit = null,
            string? sort = null,
            string? filter = null,
            DateTime? dateLastUpdated = null)
        {
            sort ??= "date_last_updated:desc";
            string s = $"http://www.giantbomb.com/api/{resource}/?api_key={_apiKey}&format=json&sort={sort}";

            if (fields != null)
                s += $"&field_list={fields}";

            if (offset > 0)
                s += $"&offset={offset}";

            if (limit != null)
                s += $"&limit={limit}";

            if (dateLastUpdated != null)
            {
                if (filter != null)
                    filter += ',';
                filter += $"date_last_updated:{dateLastUpdated:yyyy-MM-dd HH:mm:ss}|{DateTime.UtcNow.AddYears(1):yyyy-MM-dd HH:mm:ss}";
            }

            if (filter != null)
                s += $"&filter={filter}";

            return new Uri(s);
        }

        private Uri BuildDetailUri(string url, string fields)
        {
            string s = $"{url}?api_key={_apiKey}&format=json";
            if (fields != null)
                s += $"&field_list={fields}";
            return new Uri(s);
        }

        private JObject Download(Uri uri)
        {
            const int MaxRetries = 3;

            EnforceRateLimit();

            Trace.WriteLineIf(_verbose, string.Empty);
            Trace.WriteLineIf(_verbose, uri, typeof(GiantBombTableStore).Name);

            string json;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    json = _webClient.DownloadString(uri);
                    break;
                }
                catch (WebException ex) when (attempt < MaxRetries)
                {
                    Console.WriteLine($"Retrying after failed {attempt + 1} time(s) with {ex.Status}: {ex.Message}");
                    EnforceRateLimit();
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Pow(3, attempt)));
                }
            }

            var obj = JObject.Parse(json);

            if (obj["status_code"].Value<int>() != 1)
                throw new Exception($"GiantBomb returned error {obj["status_code"]} '{obj["error"]}' for '{uri}'");

            return obj;
        }

        private void EnforceRateLimit()
        {
            if (DateTime.UtcNow < _nextRequest)
                Thread.Sleep(_nextRequest - DateTime.UtcNow);
            _nextRequest = DateTime.UtcNow + TimeSpan.FromMilliseconds(1001);
        }

        private class InternalWebClient : WebClient
        {
            private readonly string _userAgent;

            internal InternalWebClient(string userAgent)
            {
                _userAgent = userAgent;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = (HttpWebRequest)base.GetWebRequest(address);
                request.Accept = "text/html, application/xhtml+xml, image/jxr, */*";
                request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
                request.Headers.Set(HttpRequestHeader.AcceptEncoding, "gzip, deflate");
                request.Headers.Set(HttpRequestHeader.AcceptLanguage, "en-US");
                request.UserAgent = _userAgent;
                return request;
            }
        }
    }
}
