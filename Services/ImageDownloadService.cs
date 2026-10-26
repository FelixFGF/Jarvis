using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Jarvis.Services;

public sealed record DownloadedImage(byte[] Bytes, string Extension, string MediaType);

public sealed class ImageDownloadService(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsClient = client is null;
    private const long MaximumBytes = 100L * 1024 * 1024;

    public async Task<DownloadedImage> FetchAsync(string imageUrl, CancellationToken token)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)
            throw new InvalidDataException("The image URL is not a safe public HTTPS address.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jarvis", "1.1"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumBytes) throw new InvalidDataException("The image is larger than 100 MB.");
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var extension = mediaType switch
        {
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp",
            "image/bmp" => ".bmp", "image/tiff" => ".tiff", "image/svg+xml" => ".svg", _ => ""
        };
        if (extension.Length == 0) throw new InvalidDataException("The server did not return a supported image media type.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("The image is larger than 100 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (output.Length == 0) throw new InvalidDataException("The image response was empty.");
        return new(output.ToArray(), extension, mediaType);
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
