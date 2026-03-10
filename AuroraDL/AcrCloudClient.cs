using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace auroradl;

internal record TrackMetadata(string Title, string Artist, string? Isrc)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

internal sealed class AcrCloudClient : IDisposable
{
    private readonly string _host;
    private readonly string _accessKey;
    private readonly string _accessSecret;
    private readonly HttpClient _http;

    private AcrCloudClient(string host, string accessKey, string accessSecret)
    {
        _host = host.Trim();
        _accessKey = accessKey.Trim();
        _accessSecret = accessSecret.Trim();
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    public static AcrCloudClient? FromEnvironment()
    {
        string host = Environment.GetEnvironmentVariable("ACR_HOST")
            ?? Environment.GetEnvironmentVariable("ACRCLOUD_HOST")
            ?? "";
        string key = Environment.GetEnvironmentVariable("ACR_ACCESS_KEY")
            ?? Environment.GetEnvironmentVariable("ACRCLOUD_ACCESS_KEY")
            ?? "";
        string secret = Environment.GetEnvironmentVariable("ACR_ACCESS_SECRET")
            ?? Environment.GetEnvironmentVariable("ACRCLOUD_ACCESS_SECRET")
            ?? "";

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new AcrCloudClient(host, key, secret);
    }

    public async Task<TrackMetadata?> TryIdentifyAsync(float[] monoSamples, int sampleRate)
    {
        if (monoSamples.Length == 0 || sampleRate <= 0) return null;

        byte[] wav = BuildWavPcm16(monoSamples, sampleRate);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string method = "POST";
        string uri = "/v1/identify";
        string dataType = "audio";
        string sigVersion = "1";
        string toSign = $"{method}\n{uri}\n{_accessKey}\n{dataType}\n{sigVersion}\n{ts}";
        string signature = Sign(toSign, _accessSecret);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_accessKey), "access_key");
        form.Add(new StringContent(dataType), "data_type");
        form.Add(new StringContent(sigVersion), "signature_version");
        form.Add(new StringContent(ts.ToString()), "timestamp");
        form.Add(new StringContent(signature), "signature");
        form.Add(new StringContent(wav.Length.ToString()), "sample_bytes");
        var sampleContent = new ByteArrayContent(wav);
        sampleContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(sampleContent, "sample", "sample.wav");

        string endpoint = _host.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? $"{_host.TrimEnd('/')}{uri}"
            : $"https://{_host.TrimEnd('/')}{uri}";

        using var resp = await _http.PostAsync(endpoint, form).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseMetadata(json);
    }

    private static TrackMetadata? ParseMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status)) return null;
            if (!status.TryGetProperty("code", out var codeEl)) return null;
            int code = codeEl.GetInt32();
            if (code != 0) return null;
            if (!root.TryGetProperty("metadata", out var metadata)) return null;
            if (!metadata.TryGetProperty("music", out var music) || music.GetArrayLength() == 0) return null;
            var first = music[0];

            string title = first.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string artist = "";
            if (first.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
            {
                artist = artists[0].TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            }

            string? isrc = null;
            if (first.TryGetProperty("external_ids", out var extIds))
            {
                if (extIds.TryGetProperty("isrc", out var isrcEl)) isrc = isrcEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(title)) return null;
            
            return new TrackMetadata(title, artist, isrc);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] BuildWavPcm16(float[] monoSamples, int sampleRate)
    {
        int channels = 1;
        int bitsPerSample = 16;
        int bytesPerSample = bitsPerSample / 8;
        int dataBytes = monoSamples.Length * bytesPerSample;
        int byteRate = sampleRate * channels * bytesPerSample;
        int blockAlign = channels * bytesPerSample;

        byte[] wav = new byte[44 + dataBytes];
        void WriteAscii(int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(wav, off);
        void WriteInt32(int off, int val) => BitConverter.GetBytes(val).CopyTo(wav, off);
        void WriteInt16(int off, short val) => BitConverter.GetBytes(val).CopyTo(wav, off);

        WriteAscii(0, "RIFF");
        WriteInt32(4, 36 + dataBytes);
        WriteAscii(8, "WAVE");
        WriteAscii(12, "fmt ");
        WriteInt32(16, 16);
        WriteInt16(20, 1);
        WriteInt16(22, (short)channels);
        WriteInt32(24, sampleRate);
        WriteInt32(28, byteRate);
        WriteInt16(32, (short)blockAlign);
        WriteInt16(34, (short)bitsPerSample);
        WriteAscii(36, "data");
        WriteInt32(40, dataBytes);

        int p = 44;
        for (int i = 0; i < monoSamples.Length; i++)
        {
            float x = Math.Clamp(monoSamples[i], -1f, 1f);
            short s = (short)Math.Round(x * short.MaxValue);
            BitConverter.GetBytes(s).CopyTo(wav, p);
            p += 2;
        }
        return wav;
    }

    private static string Sign(string text, string secret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(hash);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

