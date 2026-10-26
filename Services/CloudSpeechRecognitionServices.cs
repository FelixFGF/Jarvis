using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Jarvis.Services;

public enum CloudSpeechErrorKind { MissingCredentials, Authentication, Quota, Timeout, Connectivity, InvalidAudio, Parsing, Server, Unavailable }
public sealed class CloudSpeechException(CloudSpeechErrorKind kind, string message, Exception? inner = null) : Exception(message, inner) { public CloudSpeechErrorKind Kind { get; } = kind; }

public sealed class GroqSpeechRecognitionProvider : ISpeechRecognitionProvider, IDisposable
{
    public const string DefaultModelId = "whisper-large-v3-turbo";
    public static readonly IReadOnlyList<string> Models = [DefaultModelId, "whisper-large-v3"];
    private readonly HttpClient _http; private readonly bool _owns;
    public GroqSpeechRecognitionProvider(HttpClient? client = null) { _owns = client is null; _http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) }; }
    public string Id => SpeechRecognitionProviderRouter.Groq;
    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request) => string.IsNullOrWhiteSpace(request.GroqApiKey)
        ? new(false, "A Groq API key is required.", [])
        : !Models.Contains(request.GroqModelId ?? "", StringComparer.Ordinal)
            ? new(false, "Select a supported Groq transcription model.", [])
            : new(true, "Groq cloud transcription is ready. Recordings are uploaded only when transcription starts.", ["automatic", "de", "en"]);
    public Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken token) { token.ThrowIfCancellationRequested(); var a=GetAvailability(request); if(!a.Available) throw new CloudSpeechException(CloudSpeechErrorKind.MissingCredentials,a.Message); return Task.CompletedTask; }
    public async Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken token)
    {
        var availability=GetAvailability(request); if(!availability.Available) throw new CloudSpeechException(CloudSpeechErrorKind.MissingCredentials,availability.Message);
        var file=new FileInfo(request.AudioPath); if(!file.Exists||file.Length==0) throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"The recorded audio file is missing or empty.");
        if(file.Length>100L*1024*1024) throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"The recording exceeds Groq's 100 MB developer-tier limit.");
        try { using var message=new HttpRequestMessage(HttpMethod.Post,"https://api.groq.com/openai/v1/audio/transcriptions"); message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",request.GroqApiKey);
            await using var stream=file.OpenRead(); using var audio=new StreamContent(stream); audio.Headers.ContentType=new MediaTypeHeaderValue("audio/wav"); using var form=new MultipartFormDataContent(); form.Add(audio,"file",file.Name); form.Add(new StringContent(request.GroqModelId!),"model"); form.Add(new StringContent("json"),"response_format");
            var language=Language(request.LanguageTag); if(language is not null) form.Add(new StringContent(language),"language"); message.Content=form;
            using var response=await _http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false); Ensure(response,"Groq");
            await using var body=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var json=await JsonDocument.ParseAsync(body,cancellationToken:token).ConfigureAwait(false);
            var text=json.RootElement.TryGetProperty("text",out var n)?n.GetString()?.Trim():null; return !string.IsNullOrWhiteSpace(text)?text:throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"Groq did not recognize understandable speech."); }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;} catch(OperationCanceledException ex){throw new CloudSpeechException(CloudSpeechErrorKind.Timeout,"The Groq transcription request timed out.",ex);} catch(HttpRequestException ex) when(ex.StatusCode is null){throw new CloudSpeechException(CloudSpeechErrorKind.Connectivity,"Groq could not be reached.",ex);} catch(JsonException ex){throw new CloudSpeechException(CloudSpeechErrorKind.Parsing,"Groq returned malformed transcription JSON.",ex);}
    }
    internal static string? Language(string tag)=>string.IsNullOrWhiteSpace(tag)||tag=="automatic"?null:System.Globalization.CultureInfo.GetCultureInfo(tag).TwoLetterISOLanguageName;
    internal static void Ensure(HttpResponseMessage r,string provider){if(r.IsSuccessStatusCode)return;var k=r.StatusCode switch{HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>CloudSpeechErrorKind.Authentication,HttpStatusCode.TooManyRequests=>CloudSpeechErrorKind.Quota,HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity=>CloudSpeechErrorKind.InvalidAudio,>=HttpStatusCode.InternalServerError=>CloudSpeechErrorKind.Server,_=>CloudSpeechErrorKind.Unavailable};throw new CloudSpeechException(k,k switch{CloudSpeechErrorKind.Authentication=>$"{provider} rejected the configured credentials.",CloudSpeechErrorKind.Quota=>$"{provider} rate or usage limit was reached.",CloudSpeechErrorKind.InvalidAudio=>$"{provider} rejected the audio file.",CloudSpeechErrorKind.Server=>$"{provider} is temporarily unavailable.",_=>$"{provider} transcription failed."});}
    public void Dispose(){if(_owns)_http.Dispose();}
}

public sealed class GoogleCloudSpeechRecognitionProvider : ISpeechRecognitionProvider, IDisposable
{
    public const string DefaultModelId="short"; public static readonly IReadOnlyList<string> Models=["short","long"];
    private readonly HttpClient _http; private readonly bool _owns;
    public GoogleCloudSpeechRecognitionProvider(HttpClient? client=null){_owns=client is null;_http=client??new HttpClient{Timeout=TimeSpan.FromMinutes(2)};}
    public string Id=>SpeechRecognitionProviderRouter.Google;
    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest r){if(string.IsNullOrWhiteSpace(r.GoogleCredentialsJson)||string.IsNullOrWhiteSpace(r.GoogleProjectId))return new(false,"Google Cloud service-account credentials and project ID are required.",[]);try{using var j=JsonDocument.Parse(r.GoogleCredentialsJson);if(!j.RootElement.TryGetProperty("client_email",out _)||!j.RootElement.TryGetProperty("private_key",out _))return new(false,"The Google service-account JSON is incomplete.",[]);}catch(JsonException){return new(false,"The Google service-account JSON is invalid.",[]);}return Models.Contains(r.GoogleModelId??"",StringComparer.Ordinal)?new(true,"Google Cloud Speech-to-Text is configured. Audio is uploaded only when transcription starts.",["automatic","de","en"]):new(false,"Select a supported Google STT model.",[]);}
    public Task PrepareAsync(SpeechTranscriptionRequest r,CancellationToken token){token.ThrowIfCancellationRequested();var a=GetAvailability(r);if(!a.Available)throw new CloudSpeechException(CloudSpeechErrorKind.MissingCredentials,a.Message);return Task.CompletedTask;}
    public async Task<string> TranscribeAsync(SpeechTranscriptionRequest r,CancellationToken token)
    { var a=GetAvailability(r);if(!a.Available)throw new CloudSpeechException(CloudSpeechErrorKind.MissingCredentials,a.Message);var file=new FileInfo(r.AudioPath);if(!file.Exists||file.Length==0)throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"The recorded audio file is missing or empty.");if(file.Length>10L*1024*1024)throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"The synchronous Google request is limited to 10 MB in Jarvis.");
      try{var access=await AccessTokenAsync(r.GoogleCredentialsJson!,token).ConfigureAwait(false);var uri=$"https://speech.googleapis.com/v2/projects/{Uri.EscapeDataString(r.GoogleProjectId!)}/locations/global/recognizers/_:recognize";using var message=new HttpRequestMessage(HttpMethod.Post,uri);message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);var languages=GroqSpeechRecognitionProvider.Language(r.LanguageTag);var payload=new{config=new{autoDecodingConfig=new{},languageCodes=new[]{languages is null?"en-US":r.LanguageTag},model=r.GoogleModelId},content=Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName,token))};message.Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json");using var response=await _http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);GroqSpeechRecognitionProvider.Ensure(response,"Google Cloud");using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));var parts=new List<string>();if(json.RootElement.TryGetProperty("results",out var results))foreach(var result in results.EnumerateArray())if(result.TryGetProperty("alternatives",out var alternatives)&&alternatives.GetArrayLength()>0&&alternatives[0].TryGetProperty("transcript",out var transcript)&&!string.IsNullOrWhiteSpace(transcript.GetString()))parts.Add(transcript.GetString()!.Trim());return parts.Count>0?string.Join(" ",parts):throw new CloudSpeechException(CloudSpeechErrorKind.InvalidAudio,"Google Cloud did not recognize understandable speech.");}
      catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}catch(OperationCanceledException ex){throw new CloudSpeechException(CloudSpeechErrorKind.Timeout,"The Google Cloud transcription request timed out.",ex);}catch(HttpRequestException ex)when(ex.StatusCode is null){throw new CloudSpeechException(CloudSpeechErrorKind.Connectivity,"Google Cloud could not be reached.",ex);}catch(JsonException ex){throw new CloudSpeechException(CloudSpeechErrorKind.Parsing,"Google Cloud returned malformed JSON.",ex);} }
    private async Task<string> AccessTokenAsync(string credentials,CancellationToken token){using var json=JsonDocument.Parse(credentials);var email=json.RootElement.GetProperty("client_email").GetString()!;var key=json.RootElement.GetProperty("private_key").GetString()!;var tokenUri=json.RootElement.TryGetProperty("token_uri",out var t)?t.GetString()!:"https://oauth2.googleapis.com/token";var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var header=B64(JsonSerializer.SerializeToUtf8Bytes(new{alg="RS256",typ="JWT"}));var claim=B64(JsonSerializer.SerializeToUtf8Bytes(new{iss=email,scope="https://www.googleapis.com/auth/cloud-platform",aud=tokenUri,iat=now,exp=now+3600}));using var rsa=RSA.Create();rsa.ImportFromPem(key);var assertion=header+"."+claim+"."+B64(rsa.SignData(Encoding.ASCII.GetBytes(header+"."+claim),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));using var form=new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","urn:ietf:params:oauth:grant-type:jwt-bearer"},{"assertion",assertion}});using var response=await _http.PostAsync(tokenUri,form,token).ConfigureAwait(false);GroqSpeechRecognitionProvider.Ensure(response,"Google Cloud authentication");using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));return result.RootElement.GetProperty("access_token").GetString()??throw new CloudSpeechException(CloudSpeechErrorKind.Authentication,"Google Cloud returned no access token.");}
    private static string B64(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    public void Dispose(){if(_owns)_http.Dispose();}
}
