using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.CommunityModel;

namespace Fortiq.Assistant;

/// <summary>
/// The shape the model is required to answer in, and how that answer is read back.
/// </summary>
/// <remarks>
/// llama.cpp compiles a JSON schema into a grammar and constrains sampling to it, so the model
/// cannot emit anything else - this is not a request the model may decline. That matters more than
/// tidiness: a free-form answer has to be parsed by guessing, and every guess is a place where a
/// sentence the model wrote decides how Fortiq behaves.
///
/// The schema is deliberately small. A two-billion-parameter model held to a large nested structure
/// spends its budget satisfying the shape rather than answering, and the failure looks like a
/// well-formed reply that says nothing.
/// </remarks>
public static class AssistantResponseSchema
{
    /// <summary>The item kinds the model may use, in the exact spelling the schema permits.</summary>
    private static readonly string[] Kinds =
        [.. Enum.GetNames<SemanticItemKind>()];

    /// <summary>The JSON schema, as llama.cpp's chat endpoint expects it.</summary>
    public static JsonNode Schema { get; } = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                // Bounded, because an unbounded array is a way for a small model to loop politely
                // until it hits the token limit and the answer is cut off mid-sentence.
                ["maxItems"] = 8,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray([.. Kinds.Select(kind => (JsonNode)kind!)])
                        },
                        ["text"] = new JsonObject { ["type"] = "string" },
                        ["factRef"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("type", "text"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("items"),
        ["additionalProperties"] = false
    };

    /// <summary>What the model is told about answering in this shape.</summary>
    /// <remarks>
    /// The schema constrains the shape and cannot constrain the meaning: nothing in a grammar stops
    /// a model labelling its own invention as a Fact. That is what grounding is for, and this
    /// instruction exists so the honest path is also the easy one.
    /// </remarks>
    public const string Instruction =
        "Answer as a list of items. Use Fact only for something Fortiq recorded, and put its "
        + "[reference] in factRef. Use Explanation for what something means, Recommendation for what "
        + "the person might do, Warning for what they should be careful about, and Question when you "
        + "need to know something before you can answer. Two or three items is usually enough.";

    /// <summary>
    /// Reads a structured answer, or says plainly that it was not one.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing when the text is not the expected shape. The runtime falls
    /// back to treating the reply as prose, because an assistant that shows a person nothing because
    /// its output failed a schema is worse than one that shows them a paragraph.
    /// </remarks>
    public static AssistantResponse? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["items"] is not JsonArray array)
        {
            return null;
        }

        var items = new List<SemanticItem>();
        foreach (var node in array)
        {
            var text = node?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var kind = Enum.TryParse<SemanticItemKind>(node?["type"]?.GetValue<string>(), out var parsed)
                ? parsed
                // A label the schema should have prevented. Treated as the model talking rather than
                // as a claim about what Fortiq recorded, which is the safe direction to be wrong in.
                : SemanticItemKind.Explanation;

            var reference = node?["factRef"]?.GetValue<string>();
            items.Add(new SemanticItem(
                kind,
                text.Trim(),
                string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(' ', '[', ']')));
        }

        return items.Count == 0 ? null : new AssistantResponse(items);
    }
}
