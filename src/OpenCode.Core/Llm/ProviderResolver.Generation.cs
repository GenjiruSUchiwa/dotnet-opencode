namespace OpenCode.Core.Llm;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Schema;

internal sealed class GenerationModelResolutionException(string message, string providerId, Exception? inner = null)
    : Exception(message, inner)
{
    internal string ProviderId { get; } = providerId;
}

internal sealed class GenerationCredentialsException(Exception inner)
    : Exception("Generation credentials are unavailable", inner);

internal sealed class GenerationProviderException(string message, string providerId, Exception inner) : Exception(message, inner)
{
    internal string ProviderId { get; } = providerId;
}

public sealed partial class ProviderResolver
{
    /// <summary>Stateless ModelResolver semantics over one base-Location catalog snapshot.</summary>
    internal async Task<ResolvedModel?> ResolveGenerationAsync(ModelRef? requested, string directory, CancellationToken ct)
    {
        var snapshot = await LoadCatalogSnapshotAsync(directory, ct).ConfigureAwait(false);
        var catalog = await ProjectCatalogAsync(snapshot, ct).ConfigureAwait(false);
        // Explicit selections use model.get, including a disabled model. Implicit
        // selection uses default(), falling back only when it has no package.
        // A nonempty but unsupported package is an error, not model substitution.
        var selected = requested is not null
            ? catalog.Models.FirstOrDefault(model => model.ProviderId == requested.ProviderId && model.Id == requested.Id)
            : catalog.DefaultModel is { Package.Length: > 0 } preferred ? preferred
            : catalog.AvailableModels.FirstOrDefault(model => !string.IsNullOrEmpty(model.Package));
        if (selected is null) return null;
        StoredCredential? credential;
        // Integration.connection.resolve wraps refresh failures as Authorization;
        // this is distinct from a route's own missing/invalid authentication error.
        try { credential = await ResolveCatalogCredentialAsync(snapshot, selected.ProviderId, ct).ConfigureAwait(false); }
        catch (LlmException error) { throw new GenerationCredentialsException(error); }
        try
        {
            // Source load resolves the active connection before checking variants
            // or initializing the package. Reuse this exact credential snapshot.
            return await ResolveSelectionAsync(snapshot, (selected.ProviderId, selected.Id, requested?.Variant),
                null, ct, stateless: true, generationCredential: credential).ConfigureAwait(false);
        }
        catch (LlmException error) when (error.Reason is LlmFailure.Authentication)
        {
            throw new GenerationProviderException(error.Message, selected.ProviderId, error);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or JsonException
            || error is LlmException { Reason: LlmFailure.Unsupported or LlmFailure.InvalidRequest })
        {
            // ModelResolver maps package/configuration initialization failures to
            // UnsupportedPackageError. Preserve the original failure as the cause.
            throw new GenerationModelResolutionException(
                $"Unsupported package for {selected.ProviderId}/{selected.Id}: {selected.Package ?? "unknown"}", selected.ProviderId, error);
        }
    }
}
