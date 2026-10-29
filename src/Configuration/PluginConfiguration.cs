using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SearchAccelerator.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    // An empty answer from a healthy external provider is final; Jellyfin's database search doesn't run.
    public bool TrustEmptyExternalResults { get; set; } = true;

    public int ExternalTimeoutMilliseconds { get; set; } = 1500;

    public bool AcceleratePeopleSearch { get; set; } = true;

    // Which People search users with library, parental-rating or tag restrictions get.
    public RestrictedUserPeopleSearch RestrictedUserPeopleSearch { get; set; } = RestrictedUserPeopleSearch.Skip;

    public bool DebugLogging { get; set; }
}

public enum RestrictedUserPeopleSearch
{
    // No people results; fast.
    Skip,

    // Jellyfin's own People search, which checks what the user may see; slow but complete.
    UseJellyfin,
}
