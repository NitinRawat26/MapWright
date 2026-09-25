namespace MapWright.Ai;

/// <summary>Tries each provider in order and returns the first answer.</summary>
public sealed class FallbackAiProvider(IReadOnlyList<IAiProvider> providers) : IAiProvider
{
    public IReadOnlyList<IAiProvider> Providers { get; } = providers.Count > 0
        ? providers
        : throw new ArgumentException("At least one provider is required.", nameof(providers));

    public string Name => string.Join(" → ", Providers.Select(p => p.Name));

    public async Task<AiReply> GenerateJsonAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        var failures = new List<AiProviderException>();
        foreach (var provider in Providers)
        {
            try
            {
                return await provider.GenerateJsonAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (AiProviderException ex)
            {
                failures.Add(ex);
            }
        }

        throw new AiProviderException(string.Join("; ", failures.Select(f => f.Message)), new AggregateException(failures));
    }
}
