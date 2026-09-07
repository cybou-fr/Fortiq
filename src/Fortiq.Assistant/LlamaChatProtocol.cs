using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fortiq.Assistant;

/// <summary>
/// The request and reply shapes llama-server speaks, kept apart from the process that speaks them.
/// </summary>
/// <remarks>
/// Separate from <c>LlamaServerRuntime</c> so that what is sent and how a reply is read can be
/// tested without starting a nine-hundred-megabyte process. Everything here is a pure function of
/// its input; the runtime does the parts that need a machine.
/// </remarks>
internal static class LlamaChatProtocol
{
    /// <summary>Builds the chat request body for one question.</summary>
    /// <remarks>
    /// <c>enable_thinking</c> is false, and that is not a preference. With reasoning on, this model
    /// spends its whole token budget thinking and returns an empty answer - measured, not assumed,
    /// on the pinned model: 120 tokens of reasoning and nothing said. Spec 28 asks for non-thinking
    /// concise output as the default mode, and this is where that default is set.
    ///
    /// The temperature is low for the same reason the output is structured elsewhere: this assistant
    /// explains what a machine did, and invention is the failure mode that matters.
    /// </remarks>
    public static string BuildRequest(AssistantAsk ask, int maxTokens, bool structured = false)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        var system = structured
            ? AssistantPrompt.SystemInstruction + " " + AssistantResponseSchema.Instruction
            : AssistantPrompt.SystemInstruction;

        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = AssistantPrompt.Build(ask) }
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["stream"] = false,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }
        };

        if (structured)
        {
            // llama.cpp compiles this into a grammar and constrains sampling to it, so the shape is
            // not a request the model may decline. A free-form answer would have to be parsed by
            // guessing, and every guess is a place where a sentence the model wrote decides how
            // Fortiq behaves.
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "fortiq_assistant_response",
                    ["strict"] = true,
                    ["schema"] = AssistantResponseSchema.Schema.DeepClone()
                }
            };
        }

        return body.ToJsonString();
    }

    /// <summary>Reads the answer out of a reply, or says plainly that there was not one.</summary>
    /// <remarks>
    /// A reply with no choices, or a choice with no content, is treated as a failure rather than as
    /// an empty answer. An assistant that silently shows nothing is worse than one that says it
    /// could not answer: the person is left unable to tell the difference between "no problem
    /// found" and "this did not work".
    /// </remarks>
    public static AssistantReply ReadReply(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The assistant runtime returned something that is not JSON: {error.Message}");
        }

        if (root?["error"] is { } failure)
        {
            var detail = failure["message"]?.GetValue<string>() ?? failure.ToJsonString();
            throw new InvalidDataException($"The assistant runtime reported an error: {detail}");
        }

        if (root?["choices"] is not JsonArray { Count: > 0 } choices)
        {
            throw new InvalidDataException("The assistant runtime returned no answer.");
        }

        var choice = choices[0];
        var text = choice?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("The assistant runtime returned an empty answer.");
        }

        var truncated = string.Equals(choice?["finish_reason"]?.GetValue<string>(), "length", StringComparison.Ordinal);

        // Prose when it is prose, statements when the schema held. Falling back rather than failing:
        // an assistant that shows a person nothing because its own output missed a schema is worse
        // than one that shows them a paragraph.
        var structured = AssistantResponseSchema.Read(text);
        return new AssistantReply(structured?.Text ?? text.Trim(), truncated, structured);
    }
}
