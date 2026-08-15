using System.Text.Json;
using System.Text.Json.Nodes;
using Dytools.DeployTool.Models.Config;
using NJsonSchema;
using NJsonSchema.Generation;

// -----------------------------------------------------------------------------
// SchemaGen - generates deploy-config.schema.json from the DeployConfig models.
//
// Run from the repo root:
//   dotnet run --project tools/SchemaGen
// or specify an output path:
//   dotnet run --project tools/SchemaGen -- path/to/deploy-config.schema.json
//
// Field descriptions come from the XML doc-comments on the model (DeployTool emits
// an XML doc file that NJsonSchema reads automatically), so editors show hover help.
// -----------------------------------------------------------------------------

var outPath = args.Length > 0 ? args[0] : "deploy-config.schema.json";

var settings = new SystemTextJsonSchemaGeneratorSettings();
var schema = JsonSchema.FromType<DeployConfig>(settings);
schema.Title = "Dytools.DeployTool deploy-config.json";

var root = JsonNode.Parse(schema.ToJson())!.AsObject();

// The root uses additionalProperties:false, which would otherwise reject the "$schema"
// key that config files need in order to bind to this schema in an editor. Allow it.
if (root["properties"] is JsonObject rootProps)
{
    rootProps["$schema"] = new JsonObject
    {
        ["type"] = "string",
        ["description"] = "Path or URL of this JSON Schema, so editors bind autocomplete and validation."
    };
}

// dependentProjects accepts a single string OR an array of strings (StringOrArrayConverter).
// NJsonSchema only sees the List<string> type, so widen it to a oneOf by hand.
FixDependentProjects(root);

File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
return 0;

static void FixDependentProjects(JsonObject root)
{
    // NJsonSchema emits type definitions under "definitions" (draft-07) or "$defs".
    foreach (var key in new[] { "definitions", "$defs" })
    {
        if (root[key] is not JsonObject defs) continue;

        foreach (var (_, defNode) in defs)
        {
            if (defNode is JsonObject def &&
                def["properties"] is JsonObject props &&
                props["dependentProjects"] is JsonObject dep)
            {
                var description = dep["description"]?.GetValue<string>();

                var replacement = new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "string" },
                        new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        })
                };
                if (description is not null) replacement["description"] = description;

                props["dependentProjects"] = replacement;
            }
        }
    }
}
