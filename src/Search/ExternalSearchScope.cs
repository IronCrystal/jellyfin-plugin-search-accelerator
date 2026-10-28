using System.Threading;

namespace Jellyfin.Plugin.SearchAccelerator.Search;

/// <summary>
/// What the external providers did during one search. Jellyfin's SearchManager logs and swallows
/// provider exceptions and returns an empty list, so this is the only way to tell "nothing matched"
/// from "the provider failed".
/// </summary>
internal sealed class ExternalSearchScope
{
    private static readonly AsyncLocal<ExternalSearchScope?> _current = new();
    private int _searched;
    private int _failed;
    private int _resultCount;

    public static ExternalSearchScope? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>Gets a value indicating whether any provider accepted the query (CanSearch returned true).</summary>
    public bool Searched => Volatile.Read(ref _searched) != 0;

    /// <summary>Gets a value indicating whether any provider threw.</summary>
    public bool Failed => Volatile.Read(ref _failed) != 0;

    /// <summary>Gets how many results the providers returned before Jellyfin's user-access filtering.</summary>
    public int ResultCount => Volatile.Read(ref _resultCount);

    public void MarkSearched() => Volatile.Write(ref _searched, 1);

    public void MarkFailed() => Volatile.Write(ref _failed, 1);

    public void AddResults(int count) => Interlocked.Add(ref _resultCount, count);
}
