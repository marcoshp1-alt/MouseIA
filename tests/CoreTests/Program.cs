using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Core;

static class P
{
    static int failed;
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }

    static async Task<int> Main(string[] args)
    {
        string mock = args[0]; // http://127.0.0.1:PORT
        // Point cloud providers at the mock by rewriting the request URL.
        var tests = new[] { ProviderIds.Gemini, ProviderIds.OpenAi, ProviderIds.Claude, ProviderIds.Local };
        foreach (var provider in tests)
        {
            var req = new AiRequest
            {
                Provider = provider, ApiKey = "k", Models = new[] { "m1" },
                BaseUrl = mock + "/" + provider + "/v1",
                SystemPrompt = "sys", History = new[] { new ChatTurn(true, "olá") },
                ImagesJpegBase64 = new[] { "QUJD" },
            };
            var text = await RunViaMock(req, mock, provider);
            Check(text == "Olá, mundo!", $"{provider} stream -> '{text}'");
        }
        // Fallback: first model 404s, second answers.
        {
            var req = new AiRequest { Provider = ProviderIds.Local, Models = new[] { "missing", "m1" }, BaseUrl = mock + "/local/v1",
                SystemPrompt = "s", History = new[] { new ChatTurn(true, "x") } };
            var sb = new StringBuilder();
            var r = await AiClient.StreamAsync(req, t => sb.Append(t), CancellationToken.None);
            Check(r.UsedFallback && r.Model == "m1" && sb.ToString() == "Olá, mundo!", "fallback to next model");
        }
        // Error classification.
        try
        {
            var req = new AiRequest { Provider = ProviderIds.Local, Models = new[] { "badkey" }, BaseUrl = mock + "/local/v1",
                SystemPrompt = "s", History = new[] { new ChatTurn(true, "x") } };
            await AiClient.StreamAsync(req, _ => { }, CancellationToken.None);
            Check(false, "401 -> InvalidKey");
        }
        catch (AiException ex) { Check(ex.Kind == AiErrorKind.InvalidKey, "401 -> InvalidKey (" + ex.Kind + ")"); }
        // Connection refused.
        try
        {
            var req = new AiRequest { Provider = ProviderIds.Local, Models = new[] { "m" }, BaseUrl = "http://127.0.0.1:1/v1",
                SystemPrompt = "s", History = new[] { new ChatTurn(true, "x") } };
            await AiClient.StreamAsync(req, _ => { }, CancellationToken.None);
            Check(false, "refused -> Network");
        }
        catch (AiException ex) { Check(ex.Kind == AiErrorKind.Network, "refused -> Network (" + ex.Kind + ")"); }

        // URLs
        Check(AiClient.LocalChatUrl("http://localhost:11434") == "http://localhost:11434/v1/chat/completions", "url no path");
        Check(AiClient.LocalChatUrl("127.0.0.1:1234/v1/") == "http://127.0.0.1:1234/v1/chat/completions", "url no scheme");
        Check(AiClient.LocalModelsUrl("http://x:9379/v1/chat/completions") == "http://x:9379/v1/models", "models url");
        Check(LocalAiDiscovery.Normalize("http://localhost:11434") == "http://127.0.0.1:11434/v1", "normalize localhost");

        // Discovery against mock
        var models = await LocalAiDiscovery.ListModelsAsync(mock + "/local/v1", null, CancellationToken.None);
        Check(models != null && models.SequenceEqual(new[] { "gemma4:e4b", "qwen2.5vl:7b" }), "list models (embed skipped): " + string.Join(",", models ?? Array.Empty<string>()));
        var found = await LocalAiDiscovery.DetectAsync(mock + "/local/v1", CancellationToken.None);
        Check(found.Any(f => f.Models.Contains("gemma4:e4b")), "detect custom server");

        // Think filter across chunks
        var tf = new ThinkFilter();
        var outp = string.Concat(new[] { "<thi", "nk>abc", "</thi", "nk>Oi ", "<b>x</b>", "<", " fim" }.Select(tf.Feed));
        Check(outp == "Oi <b>x</b>< fim", "think filter: " + outp);

        // Shake detector
        var d = new ShakeDetector { Sensitivity = 5 };
        bool shook = false; long t0 = 0; int x = 500;
        for (int leg = 0; leg < 6 && !shook; leg++)
            for (int s = 0; s < 8 && !shook; s++) { x += (leg % 2 == 0 ? 12 : -12); t0 += 10; shook |= d.Add(x, 300, t0); }
        Check(shook, "shake detected");
        d = new ShakeDetector { Sensitivity = 5 }; bool falsePos = false; t0 = 0; x = 0;
        for (int i = 0; i < 300; i++) { x += 4; t0 += 8; falsePos |= d.Add(x, 300 + (i % 3), t0); } // straight line
        for (int leg = 0; leg < 6; leg++) for (int s = 0; s < 2; s++) { x += (leg % 2 == 0 ? 6 : -6); t0 += 10; falsePos |= d.Add(x, 300, t0); } // jitter
        Check(!falsePos, "no false shake on straight move/jitter");
        d = new ShakeDetector { Sensitivity = 5 }; shook = false; t0 = 0; x = 500;
        for (int leg = 0; leg < 6 && !shook; leg++) { for (int s = 0; s < 8 && !shook; s++) { x += (leg % 2 == 0 ? 12 : -12); t0 += 10; shook |= d.Add(x, 300, t0); } t0 += 400; }
        Check(!shook, "slow back-and-forth (with pauses) ignored");

        // Languages
        Check(Languages.FromCulture(new System.Globalization.CultureInfo("pt-BR")).Code == "pt-BR", "culture pt-BR");
        Check(Languages.FromCulture(new System.Globalization.CultureInfo("en-US")).Code == "en", "culture en-US");
        Check(Languages.FromCulture(new System.Globalization.CultureInfo("zh-TW")).Code == "zh-TW", "culture zh-TW");
        Check(Languages.All.Select(l => l.Code).Distinct().Count() == Languages.All.Count, "unique codes (" + Languages.All.Count + ")");

        // Settings round trip
        var path = Path.Combine(Path.GetTempPath(), "mouseia-test.json");
        var st = new AppSettings { Provider = ProviderIds.Claude, ClaudeKey = "sk-ant-x", LocalModel = "gemma4:e4b" };
        st.Save(path, new PlainProtector());
        var loaded = AppSettings.Load(path, new PlainProtector());
        Check(loaded.Provider == "claude" && loaded.ClaudeKey == "sk-ant-x" && loaded.LocalModel == "gemma4:e4b" && loaded.HasAi, "settings round trip");
        Check(!File.ReadAllText(path).Contains("\"claudeKey\""), "plain key fields not serialized");
        File.WriteAllText(path, "{garbage"); Check(AppSettings.Load(path, new PlainProtector()).Provider == "gemini", "damaged settings -> defaults");

        // Models
        Check(ModelCatalog.ModelsFor(new AppSettings { Provider = "claude", Quality = "smart" }).SequenceEqual(new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" }), "tier fallback order");

        // Documents
        var docx = Path.Combine(Path.GetTempPath(), "t.docx");
        File.Delete(docx);
        using (var z = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Olá</w:t></w:r><w:r><w:t xml:space=\"preserve\"> mundo</w:t></w:r></w:p><w:p><w:r><w:t>Linha 2</w:t></w:r></w:p></w:body></w:document>");
        }
        Check(DocumentText.Extract(docx) == "Olá mundo\nLinha 2", "docx text: " + DocumentText.Extract(docx));

        Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        return failed == 0 ? 0 : 1;
    }

    // Cloud providers have fixed URLs; send the same request to the mock and parse it like the client does.
    static async Task<string> RunViaMock(AiRequest req, string mock, string provider)
    {
        if (provider == ProviderIds.Local)
        {
            var sb = new StringBuilder();
            await AiClient.StreamAsync(req, t => sb.Append(t), CancellationToken.None);
            return sb.ToString();
        }
        var build = typeof(AiClient).GetMethod("BuildHttpRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var msg = (HttpRequestMessage)build.Invoke(null, new object[] { req, "m1" })!;
        var bodyBytes = await msg.Content!.ReadAsByteArrayAsync();
        var original = msg.RequestUri!.ToString();
        using var forward = new HttpRequestMessage(HttpMethod.Post, mock + "/" + provider + "/check?orig=" + Uri.EscapeDataString(original));
        forward.Content = new ByteArrayContent(bodyBytes);
        foreach (var h in msg.Headers) forward.Headers.TryAddWithoutValidation(h.Key, h.Value);
        using var http = new HttpClient();
        using var resp = await http.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return "HTTP " + (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
        var read = typeof(AiClient).GetMethod("ReadEvent", BindingFlags.NonPublic | BindingFlags.Static)!;
        var outText = new StringBuilder();
        await foreach (var data in SseReader.ReadEventsAsync(await resp.Content.ReadAsStreamAsync(), TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            if (data == "[DONE]") break;
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            var a = new object[] { provider, doc.RootElement, "", "" };
            outText.Append((string)read.Invoke(null, a)!);
        }
        return outText.ToString();
    }
}
