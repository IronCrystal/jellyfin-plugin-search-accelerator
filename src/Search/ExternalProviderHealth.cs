using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SearchAccelerator.Search;

/// <summary>
/// Decides whether an empty external answer can be trusted. Shadowghost's Meilisearch provider catches
/// its own errors and yields nothing when Meilisearch is down, which looks exactly like "no matches".
/// An empty answer is only trusted if the provider recently found something: a real search returned
/// results, or a probe search for a known item did.
/// </summary>
internal sealed class ExternalProviderHealth
{
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _healthyFor;
    private readonly TimeSpan _unhealthyFor;
    private readonly object _lock = new();
    private bool _healthy;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private Task<bool>? _probing;

    public ExternalProviderHealth(
        Func<CancellationToken, Task<bool>> probe,
        TimeProvider timeProvider,
        TimeSpan healthyFor,
        TimeSpan unhealthyFor)
    {
        _probe = probe;
        _timeProvider = timeProvider;
        _healthyFor = healthyFor;
        _unhealthyFor = unhealthyFor;
    }

    public void ReportHealthy() => Record(true);

    public Task<bool> IsHealthyAsync()
    {
        lock (_lock)
        {
            var age = _timeProvider.GetUtcNow() - _checkedAt;
            if (age < (_healthy ? _healthyFor : _unhealthyFor))
            {
                return Task.FromResult(_healthy);
            }

            // Concurrent empty searches (the web client sends several per keystroke) share one probe.
            if (_probing is null || _probing.IsCompleted)
            {
                _probing = RunProbeAsync();
            }

            return _probing;
        }
    }

    private async Task<bool> RunProbeAsync()
    {
        bool healthy;
        try
        {
            // Not tied to any one request: a cancelled request mustn't fail the probe the others wait on.
            healthy = await _probe(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            healthy = false;
        }

        Record(healthy);
        return healthy;
    }

    private void Record(bool healthy)
    {
        lock (_lock)
        {
            _healthy = healthy;
            _checkedAt = _timeProvider.GetUtcNow();
        }
    }
}
