using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace BeanotchDock.Services;

public sealed record GitHubAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url);

public sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

[JsonSerializable(typeof(GitHubRelease[]))]
internal sealed partial class ReleaseJson : JsonSerializerContext;

// New versions from the project's GitHub releases. Installing downloads the setup for this PC's processor and runs it
// quietly: it closes the dock, replaces it and opens the new one (the installer's last step), all in a few seconds.
public sealed class Updater
{
    private const string Repository = "giankbr/beanotch-dock";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Current { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public string? Latest { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }
    public bool Failed { get; private set; }
    private string? download;

    public bool UpdateAvailable => Latest is not null && Newer(Latest, Current);

    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        Failed = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=10");
            request.Headers.UserAgent.ParseAdd("BeanotchDock");
            using var response = await Http.SendAsync(request);
            var releases = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(ReleaseJson.Default.GitHubReleaseArray) : null;
            var name = $"BeanotchDock-Setup-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.exe";
            // The newest release that carries this PC's installer (CI adds it a few minutes after a release is made).
            var release = releases?.FirstOrDefault(r => r.Assets.Any(a => a.Name == name));
            if (release is null) { Failed = releases is null; return; }
            Latest = release.Tag.TrimStart('v');
            download = release.Assets.First(a => a.Name == name).Url;
            CheckedAt = DateTimeOffset.Now;
        }
        catch (Exception) { Failed = true; }
        finally { Checking = false; }
    }

    // Downloads and starts the installer; the caller quits so it can replace the files.
    public async Task<bool> DownloadAndRunAsync()
    {
        if (download is null || Installing) return false;
        Installing = true;
        try
        {
            var file = Path.Combine(Path.GetTempPath(), $"BeanotchDock-Setup-{Latest}.exe");
            await using (var stream = await Http.GetStreamAsync(download))
            await using (var output = File.Create(file))
                await stream.CopyToAsync(output);
            Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            return true;
        }
        catch (Exception) { Installing = false; Failed = true; return false; }
    }

    public static bool Newer(string a, string b)
    {
        int[] Parts(string v) => [.. v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0)];
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }
}
