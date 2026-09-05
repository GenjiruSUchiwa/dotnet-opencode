using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Build.Framework;

public sealed class ImportPublicModels : Microsoft.Build.Utilities.Task
{
    [Required] public string SourceFile { get; set; } = "";
    [Required] public string DestinationFile { get; set; } = "";

    public override bool Execute()
    {
        var bytes = File.ReadAllBytes(SourceFile);
        JObject source;
        using (var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes))) { DateParseHandling = DateParseHandling.None })
            source = JObject.Load(reader);
        var providers = new JObject();
        var modelCount = 0;
        var eligible = 0;
        var modes = 0;
        foreach (var pair in source.Properties())
        {
            var input = pair.Value as JObject ?? throw new InvalidDataException("Invalid public provider metadata.");
            var provider = Pick(input, "id", "name", "api", "npm", "env");
            var models = new JObject();
            foreach (var model in ((JObject)input["models"]).Properties())
            {
                var value = Pick((JObject)model.Value, "id", "name", "family", "release_date", "attachment", "reasoning", "reasoning_options",
                    "temperature", "tool_call", "interleaved", "cost", "limit", "modalities", "experimental", "status", "provider");
                models[model.Name] = value;
                modelCount++;
                if (input["id"].ToString() != "azure-cognitive-services" && input["id"].ToString() != "google-vertex-anthropic"
                    && value["status"]?.ToString() != "deprecated")
                {
                    eligible++;
                    if (value["experimental"]?["modes"] is JObject modeMap) modes += modeMap.Count;
                }
            }
            provider["models"] = models;
            providers[pair.Name] = provider;
        }
        var removed = Scrub(providers);
        string digest;
        using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        var resource = new JObject
        {
            ["provenance"] = new JObject
            {
                ["source"] = "https://models.opencode.ai/api.json",
                ["repositoryPath"] = "packages/core/src/models-dev/snapshot.txt",
                ["sourceSha256"] = digest,
                ["providerCount"] = providers.Count, ["sourceModelCount"] = modelCount,
                ["eligibleBaseModelCount"] = eligible, ["eligibleModeCount"] = modes,
                ["credentialFieldsOmitted"] = removed
            },
            ["catalog"] = providers
        };
        Directory.CreateDirectory(Path.GetDirectoryName(DestinationFile));
        File.WriteAllText(DestinationFile, resource.ToString(Formatting.None));
        Log.LogMessage(MessageImportance.High,
            "Public catalog imported: {0} providers, {1} source models, {2} eligible base models, {3} modes; {4} credential fields omitted. Source SHA-256: {5}",
            providers.Count, modelCount, eligible, modes, removed, digest);
        return true;
    }

    private static JObject Pick(JObject value, params string[] fields)
    {
        var result = new JObject();
        foreach (var field in fields) if (value[field] != null) result[field] = value[field].DeepClone();
        return result;
    }

    private static int Scrub(JToken value)
    {
        var count = 0;
        if (value is JObject map)
            foreach (var key in map.Properties().Select(item => item.Name).ToArray())
            {
                var normalized = key.Replace("-", "").Replace("_", "").ToLowerInvariant();
                if (new[] { "apikey", "xapikey", "xgoogapikey", "accesstoken", "refreshtoken", "authorization", "proxyauthorization", "password", "secret", "clientsecret", "token" }.Contains(normalized))
                { map.Remove(key); count++; }
                else if (map[key] != null) count += Scrub(map[key]);
            }
        if (value is JArray array)
            foreach (var item in array) if (item != null) count += Scrub(item);
        return count;
    }
}
