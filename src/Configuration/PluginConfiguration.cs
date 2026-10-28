using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SearchAccelerator.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    // An empty answer from a healthy external provider is final; Jellyfin's database search doesn't run.
    public bool TrustEmptyExternalResults { get; set; } = true;

    public int ExternalTimeoutMilliseconds { get; set; } = 1500;

    public bool AcceleratePeopleSearch { get; set; } = true;

    public bool DebugLogging { get; set; }
}
