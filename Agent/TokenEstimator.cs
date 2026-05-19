using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Daggeragent.Agent;

/// <summary>
/// Counts tokens for compression-trigger and context-budget decisions. Uses Tiktoken
/// (o200k_base, gpt-4o-style) for any model — that's accurate for OpenAI-family models
/// and a tight approximation for everything else (Qwen, Llama, etc.). Per-model BPE
/// would be more precise but isn't necessary for "is it time to compress" decisions.
/// </summary>
public sealed class TokenEstimator
{
    private static readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    public int Estimate(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var msg in messages)
        {
            // Per-message framing overhead (role + separator tokens) is ~4 tokens in OpenAI's accounting.
            total += 4;
            if (!string.IsNullOrEmpty(msg.Text))
                total += _tokenizer.CountTokens(msg.Text);

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        total += _tokenizer.CountTokens(t.Text);
                        break;
                    case FunctionCallContent fc:
                        total += _tokenizer.CountTokens(fc.Name ?? "");
                        if (fc.Arguments is not null)
                            total += _tokenizer.CountTokens(System.Text.Json.JsonSerializer.Serialize(fc.Arguments));
                        break;
                    case FunctionResultContent fr:
                        if (fr.Result is not null)
                            total += _tokenizer.CountTokens(fr.Result.ToString() ?? "");
                        break;
                }
            }
        }
        return total;
    }

    public int CountTokens(string text) => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}
