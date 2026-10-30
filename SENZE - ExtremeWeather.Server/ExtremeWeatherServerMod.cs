using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using Version = SemanticVersioning.Version;

namespace SenzeExtremeWeather.Server;

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 100)]
public sealed class ExtremeWeatherServerMod : IOnLoad
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/notSENZE/ExtremeWeather/releases/latest";

    private readonly ISptLogger<ExtremeWeatherServerMod> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ExtremeWeatherServerMod(
        ISptLogger<ExtremeWeatherServerMod> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _logger.Success($"{ModInfo.LogPrefix} Server component {ModInfo.Version} loaded.");
        _ = CheckForUpdateAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var httpClient = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd($"SENZE-ExtremeWeather/{ModInfo.Version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(
                cancellationToken: timeout.Token);
            if (release == null || release.Draft || release.Prerelease)
            {
                return;
            }

            var tag = release.TagName.Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[1..];
            }

            if (!Version.TryParse(ModInfo.Version, out var currentVersion)
                || !Version.TryParse(tag, out var latestVersion)
                || latestVersion <= currentVersion)
            {
                return;
            }

            _logger.Warning(
                $"{ModInfo.LogPrefix} A new version is available on GitHub. "
                + $"Installed: {currentVersion} | Latest: {latestVersion}");
            _logger.Warning($"{ModInfo.LogPrefix} {release.PageUrl}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string PageUrl { get; init; } = ModInfo.RepositoryUrl + "/releases";

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }
    }
}
