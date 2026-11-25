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
        var tests = new[] { ProviderIds.Gemini, ProviderIds.OpenAi, ProviderIds.Claude, ProviderIds.Grok, ProviderIds.Local,
            ProviderIds.OpenCodeZen, ProviderIds.OpenCodeGo, ProviderIds.Compatible };
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
        // A compatible endpoint whose model can't read images: the answer must still come.
        {
            var req = new AiRequest { Provider = ProviderIds.Compatible, ApiKey = "k", Models = new[] { "textonly" },
                BaseUrl = mock + "/compatible/v1", SystemPrompt = "s", History = new[] { new ChatTurn(true, "olá") },
                ImagesJpegBase64 = new[] { "QUJD" } };
            var sb = new StringBuilder();
            await AiClient.StreamAsync(req, t => sb.Append(t), CancellationToken.None);
            Check(sb.ToString() == "Olá, mundo!", "image refused -> retried without it ('" + sb + "')");
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
        bool Shake(int sensitivity, int legs, int stepPx, int steps, int stepMs, int dy = 0)
        {
            var det = new ShakeDetector { Sensitivity = sensitivity };
            long t = 0; int px = 800, py = 300; bool hit = false;
            for (int leg = 0; leg < legs && !hit; leg++)
                for (int st = 0; st < steps && !hit; st++) { px += (leg % 2 == 0 ? stepPx : -stepPx); py += dy; t += stepMs; hit |= det.Add(px, py, t); }
            return hit;
        }
        Check(Shake(5, 7, 15, 10, 8), "normal shake detected (sens 5)");
        Check(!Shake(1, 7, 15, 10, 8), "same shake NOT enough at sens 1");
        Check(Shake(1, 8, 22, 9, 8), "strong fast shake works at sens 1");
        Check(Shake(10, 5, 8, 7, 10), "light shake at sens 10");
        Check(!Shake(5, 8, 15, 10, 8, dy: 12), "diagonal scribble ignored");
        var d = new ShakeDetector { Sensitivity = 5 };
        bool shook = false; long t0 = 0; int x = 500;
        d = new ShakeDetector { Sensitivity = 5 }; bool falsePos = false; t0 = 0; x = 0;
        for (int i = 0; i < 300; i++) { x += 4; t0 += 8; falsePos |= d.Add(x, 300 + (i % 3), t0); } // straight line
        for (int leg = 0; leg < 6; leg++) for (int s = 0; s < 2; s++) { x += (leg % 2 == 0 ? 6 : -6); t0 += 10; falsePos |= d.Add(x, 300, t0); } // jitter
        Check(!falsePos, "no false shake on straight move/jitter");
        d = new ShakeDetector { Sensitivity = 5 }; shook = false; t0 = 0; x = 500;
        for (int leg = 0; leg < 6 && !shook; leg++) { for (int s = 0; s < 8 && !shook; s++) { x += (leg % 2 == 0 ? 12 : -12); t0 += 10; shook |= d.Add(x, 300, t0); } t0 += 400; }
        Check(!shook, "slow back-and-forth (with pauses) ignored");
        Check(!Shake(5, 7, 15, 10, 30), "slow wide waving ignored");
        // Text blocks: a title, a two-line paragraph and two buttons on one row become 4 blocks.
        OcrBox W(string t, double x, double y, double w, double h = 20) => new(t, x, y, w, h);
        var pieces = new List<OcrBox>();
        pieces.AddRange(TextBlocks.SplitLine(new[] { W("Big", 10, 10, 60, 40), W("Title", 80, 10, 100, 40) }));
        pieces.AddRange(TextBlocks.SplitLine(new[] { W("first", 10, 80, 50), W("line", 66, 80, 40) }));
        pieces.AddRange(TextBlocks.SplitLine(new[] { W("second", 10, 104, 60), W("line", 76, 104, 40) }));
        pieces.AddRange(TextBlocks.SplitLine(new[] { W("Cancel", 10, 200, 60), W("OK", 300, 200, 30) }));
        var grouped = TextBlocks.Group(pieces);
        Check(grouped.Count == 4, $"text blocks grouped ({grouped.Count})");
        Check(grouped.Any(b => b.Text == "first line\nsecond line" && b.Height > 40), "paragraph lines joined");
        Check(grouped.Any(b => b.Text == "Big Title"), "title kept apart");
        Check(grouped.Any(b => b.Text == "OK") && grouped.Any(b => b.Text == "Cancel"), "separate buttons split");
        Check(AppSettings.IsAction("lens"), "lens action");
        var parsed = Prompts.ParseBatch("Aqui está:\n[1] Firmware\n[2] Número de série >>>\n[3] Recursos que\ncontinuam\n[9] extra", 3);
        Check(parsed[0] == "Firmware" && parsed[1] == "Número de série" && parsed[2] == "Recursos que continuam", "batch reply parsed");
        Check(Prompts.ParseBatch("[2] só dois", 3)[0] == null, "missing batch item stays empty");
        Check(Prompts.TranslateBatchRequest(Languages.Resolve("pt", System.Globalization.CultureInfo.InvariantCulture), new[] { "Hello\nworld", "OK" }).Contains("[1] Hello world\n[2] OK"), "batch request numbered");
        Check(!Prompts.IsTranslatable("•") && !Prompts.IsTranslatable(">>> 42") && Prompts.IsTranslatable("Save"), "symbols skipped");
        Check(grouped.First(b => b.Text.StartsWith("first")).Lines == 2, "block line count");
        Check(ModelCatalog.ModelsFor(new AppSettings { Provider = "grok", Quality = "smart" }).SequenceEqual(new[] { "grok-4.7", "grok-4.5" }), "grok models");

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
        var goSettings = new AppSettings { Provider = ProviderIds.OpenCodeGo, GoKey = "go-x", GoModel = "m-1" };
        goSettings.Save(path, new PlainProtector());
        var goLoaded = AppSettings.Load(path, new PlainProtector());
        Check(goLoaded.Provider == ProviderIds.OpenCodeGo && goLoaded.GoKey == "go-x" && goLoaded.GoModel == "m-1" && goLoaded.HasAi,
            "opencode-go settings round trip");
        Check(ModelCatalog.ModelsFor(goLoaded).SequenceEqual(new[] { "m-1" }), "chosen model used as is");
        Check(new AppSettings { Provider = ProviderIds.Compatible, CompatibleUrl = "https://x/v1", CompatibleModel = "m" }.HasAi,
            "compatible needs only address and model");
        Check(!new AppSettings { Provider = ProviderIds.Compatible }.HasAi, "compatible without address has no AI");
        Check(AiClient.ModelsUrlFor(ProviderIds.OpenCodeZen, "") == "https://opencode.ai/inference/v1/models" &&
              AiClient.ModelsUrlFor(ProviderIds.OpenCodeGo, "") == "https://opencode.ai/inference/go/v1/models" &&
              AiClient.ModelsUrlFor(ProviderIds.Compatible, "http://x:1/v1") == "http://x:1/v1/models", "endpoints");
        Check(AiClient.IsPrivateHost("http://192.168.100.3:8080/v1") && !AiClient.IsPrivateHost("https://api.openai.com/v1"),
            "private host detection");
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

        // Translation echo detection
        var src = "Keep your computer clean and fast with Total PC Cleaner. It lets you clean your PC's cache and big files.";
        Check(Prompts.LooksUntranslated(src, src), "echo detected");
        Check(Prompts.LooksUntranslated(src, "Keep your computer clean and fast with Total PC Cleaner. It lets you clean your PC cache and big files!"), "near-echo detected");
        Check(!Prompts.LooksUntranslated(src, "Mantenha seu computador limpo e rápido com o Total PC Cleaner. Ele permite limpar o cache e arquivos grandes do seu PC."), "real translation accepted");
        Check(!Prompts.LooksUntranslated("Hello there", "Hello there"), "short text not judged");
        var pt = Languages.Find("pt-BR")!;
        var treq = Prompts.TranslateRequest(pt, src, true);
        Check(treq.Contains("<<<") && treq.Contains("Portuguese (Brazil)") && !Prompts.TranslateSystem(pt).Contains("unchanged"), "translate prompt shape");
        // Settings defaults for new options
        var defs = new AppSettings();
        Check(defs.GlassEffect && defs.GlassOpacity == 0.85 && defs.LocalMode == "file", "new defaults");
        Check(!new AppSettings { Provider = "local", LocalMode = "file" }.HasAi && new AppSettings { Provider = "local", LocalMode = "file", LocalModelFile = "x.gguf" }.HasAi, "HasAi for model file");
        var n = new AppSettings { GlassOpacity = 5, LocalMode = "zzz", LocalBackend = "x" }; n.Normalize();
        Check(n.GlassOpacity == 1.0 && n.LocalMode == "file" && n.LocalBackend == "auto", "normalize new options");

        // Updates
        var rel = "{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"body\":\"Novidades\",\"html_url\":\"https://github.com/x/r/releases/tag/v1.2.0\",\"assets\":[" +
                  "{\"name\":\"MouseIA-Setup-1.2.0.exe\",\"browser_download_url\":\"https://x/setup.exe\"}," +
                  "{\"name\":\"MouseIA-Portable-1.2.0.exe\",\"browser_download_url\":\"https://x/portable.exe\"}]}";
        var up = UpdateInfo.Parse(rel)!;
        Check(up.Version == new Version(1, 2, 0) && up.SetupUrl == "https://x/setup.exe" && up.PortableUrl == "https://x/portable.exe" && up.Notes == "Novidades", "parse release");
        Check(up.IsNewerThan(new Version(1, 1, 1)) && !up.IsNewerThan(new Version(1, 2, 0)) && !up.IsNewerThan(new Version(1, 2, 0, 0)), "version compare");
        Check(UpdateInfo.ParseVersion("v2") == new Version(2, 0, 0) && UpdateInfo.ParseVersion("1.3.1-beta") == new Version(1, 3, 1) && UpdateInfo.ParseVersion("latest") == null, "parse tags");
        Check(UpdateInfo.Parse(rel.Replace("\"prerelease\":false", "\"prerelease\":true")) == null, "skip prerelease");
        // New settings defaults
        var a = new AppSettings();
        Check(a.LeftDragAction == "ask" && a.RightDragAction == "translate" && a.ScrollAction == "none" && a.MonitorMode == "all" && a.AutoUpdate && a.VoiceId == "auto", "action/monitor/update defaults");
        var bad = new AppSettings { LeftDragAction = "x", ScrollAction = "" }; bad.Normalize();
        Check(bad.LeftDragAction == "ask" && bad.ScrollAction == "none", "normalize actions");

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
