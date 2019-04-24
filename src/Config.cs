﻿using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GiantBombDataTool
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class Config
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool? Verbose { get; set; }
    }

    public class StoreConfig : Config
    {
        public Dictionary<string, Config> Tables { get; } = new Dictionary<string, Config>();
    }
}
