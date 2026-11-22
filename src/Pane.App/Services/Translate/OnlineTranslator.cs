using System.Net.Http;
using System.Text;
using System.Text.Json;
using Pane.Localization;

namespace Pane.Services.Translate;

/// <summary>一次在线翻译的结果：命中的引擎、译文、检测到的原文语言。</summary>
internal sealed record OnlineTranslation(bool Ok, string Text, string? DetectedSource, string Engine, string? Error);

/// <summary>免密钥在线翻译引擎 + 回退链（2026-11-22，参照 SnowAir FreeWebTranslate 的公开接口）。
/// 引擎：有道（aidemo.youdao.com/trans 演示接口）→ 必应（/translator 页取凭证 + /ttranslatev3）→ 谷歌
/// （translate.googleapis.com/translate_a/single?client=gtx）。用户选中的引擎优先，第一个成功即用；
/// 没有自备密钥服务（不做百度官方 API）。AI 模型翻译不在此链——按量计费，不参与静默回退，由翻译窗直接调用。
/// 这些是非官方/演示接口：原文会发送到对应服务，随时可能失效；失败沿链回退并在界面上如实报错。</summary>
internal static class OnlineTranslator
{
    public const string Youdao = "youdao", Bing = "bing", Google = "google";
    static readonly string[] Fallback = [Youdao, Bing, Google];

    static readonly HttpClient client = CreateClient();

    static HttpClient CreateClient()
    {
        var created = new HttpClient(new HttpClientHandler { UseCookies = true, UseDefaultCredentials = false });
        created.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        return created;
    }

    public static bool IsOnline(string engine) => engine is Youdao or Bing or Google;

    /// <summary>引擎显示名（底栏与状态提示用）。</summary>
    public static string NameOf(string engine) => engine switch
    {
        Youdao => Strings.Translate_EngineYoudao,
        Bing => Strings.Translate_EngineBing,
        Google => Strings.Translate_EngineGoogle,
        _ => Strings.Translate_EngineAi,
    };

    /// <summary>回退链：用户选中的在线引擎优先，其后 有道 → 必应 → 谷歌。取消（用户点停止）不算失败，直接终止。</summary>
    public static async Task<OnlineTranslation> TranslateAsync(string text, string target, string preferred, CancellationToken token)
    {
        var chain = Fallback.Where(engine => engine != preferred).Prepend(preferred).ToArray();
        OnlineTranslation last = new(false, "", null, chain[^1], null);
        foreach (var engine in chain)
        {
            token.ThrowIfCancellationRequested();
            var result = engine switch
            {
                Youdao => await YoudaoAsync(text, token).ConfigureAwait(false),
                Bing => await BingAsync(text, target, token).ConfigureAwait(false),
                _ => await GoogleAsync(text, target, token).ConfigureAwait(false),
            };
            if (result.Ok) return result with { Engine = engine };
            last = result;
        }
        return last;
    }

    // ---------------- 有道：官网 AI 演示接口，自动语言对，免密钥 ----------------

    static async Task<OnlineTranslation> YoudaoAsync(string text, CancellationToken token)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "Auto",
                ["to"] = "Auto",
            });
            using var response = await client.PostAsync("https://aidemo.youdao.com/trans", content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("translation", out var translation) &&
                translation.ValueKind == JsonValueKind.Array && translation.GetArrayLength() > 0 &&
                translation[0].ValueKind == JsonValueKind.String)
            {
                var result = translation[0].GetString() ?? "";
                if (result.Length > 0) return new(true, result, null, Youdao, null);
            }
            return new(false, "", null, Youdao, Strings.Translate_Failed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        {
            return new(false, "", null, Youdao, error.Message);
        }
    }

    // ---------------- 谷歌：免费 web 接口（client=gtx），无密钥 ----------------

    static async Task<OnlineTranslation> GoogleAsync(string text, string target, CancellationToken token)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" +
                GoogleCode(target) + "&dt=t&q=" + Uri.EscapeDataString(text);
            using var response = await client.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            // 响应是嵌套数组：[ [ [译文, 原文, ...], ... ], 检测语言, ... ]
            var segments = json.RootElement;
            if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() == 0) return Fail();
            var pieces = segments[0];
            if (pieces.ValueKind != JsonValueKind.Array) return Fail();
            var translated = new StringBuilder();
            foreach (var piece in pieces.EnumerateArray())
                if (piece.ValueKind == JsonValueKind.Array && piece.GetArrayLength() > 0 &&
                    piece[0].ValueKind == JsonValueKind.String)
                    translated.Append(piece[0].GetString());
            if (translated.Length == 0) return Fail();
            string? detected = segments.GetArrayLength() > 2 && segments[2].ValueKind == JsonValueKind.Array &&
                segments[2].GetArrayLength() > 0 && segments[2][0].ValueKind == JsonValueKind.String
                ? segments[2][0].GetString() : null;
            return new(true, translated.ToString(), detected, Google, null);

            OnlineTranslation Fail() => new(false, "", null, Google, Strings.Translate_Failed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        {
            return new(false, "", null, Google, error.Message);
        }
    }

    // ---------------- 必应：/translator 页取凭证 + /ttranslatev3，无密钥 ----------------
    // 凭证与站点绑定：页面被重定向到哪个站（cn.bing.com / www.bing.com），POST 就必须发到哪个站，
    // 否则接口回 200 + 空体。失败作废凭证换下一个站点重新取（SnowAir 同款语义）。

    sealed record BingCredentials(string Host, string Key, string Token, string Ig, string Iid);
    static readonly object bingGate = new();
    static BingCredentials? bingCredentials;

    static async Task<OnlineTranslation> BingAsync(string text, string target, CancellationToken token)
    {
        OnlineTranslation last = new(false, "", null, Bing, null);
        foreach (var site in new[] { "https://cn.bing.com", "https://www.bing.com" })
        {
            token.ThrowIfCancellationRequested();
            BingCredentials? credentials;
            lock (bingGate) credentials = bingCredentials;
            if (credentials == null)
            {
                credentials = await FetchBingCredentialsAsync(site, token).ConfigureAwait(false);
                if (credentials != null) lock (bingGate) bingCredentials = credentials;
            }
            if (credentials == null)
            {
                last = last with { Error = Strings.Translate_Failed };
                continue;
            }
            var result = await BingOnceAsync(credentials, text, target, token).ConfigureAwait(false);
            if (result.Ok) return result;
            last = result;
            // 空体/异常 = 凭证与站点不匹配或已过期：作废重取（SnowAir 同款）。
            lock (bingGate) bingCredentials = null;
        }
        return last;
    }

    static async Task<BingCredentials?> FetchBingCredentialsAsync(string site, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, site + "/translator");
            request.Headers.Accept.ParseAdd("application/json,text/plain,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            // 凭证与站点绑定：跟随重定向后的最终地址。
            var host = response.RequestMessage?.RequestUri is { } final ? final.Scheme + "://" + final.Host : site;
            var key = ReadAbuseHelper(page);
            if (key is not { } pair) return null;
            var ig = FindQuotedAfter(page, "IG", ':');
            var iid = FindQuotedAfter(page, "data-iid", '=');
            if (ig == null || iid == null) return null;
            return new BingCredentials(host, pair.Key, pair.Token, ig, iid);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return null;
        }
    }

    static async Task<OnlineTranslation> BingOnceAsync(BingCredentials credentials, string text, string target, CancellationToken token)
    {
        try
        {
            var body = "text=" + Uri.EscapeDataString(text)
                + "&fromLang=auto-detect&to=" + Uri.EscapeDataString(BingCode(target))
                + "&token=" + Uri.EscapeDataString(credentials.Token)
                + "&key=" + Uri.EscapeDataString(credentials.Key);
            using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var request = new HttpRequestMessage(HttpMethod.Post, credentials.Host + "/ttranslatev3?IG=" + credentials.Ig + "&IID=" + credentials.Iid)
            { Content = content };
            request.Headers.Accept.ParseAdd("application/json,text/plain,*/*;q=0.8");
            request.Headers.Referrer = new Uri(credentials.Host + "/translator");
            request.Headers.TryAddWithoutValidation("Origin", credentials.Host);
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bodyText = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            // 凭证与站点不匹配时 Bing 回 200 + 空体。
            if (bodyText.Length == 0) return new(false, "", null, Bing, Strings.Translate_Failed);
            using var json = JsonDocument.Parse(bodyText);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object)
            {
                var head = root[0];
                if (head.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number)
                    return new(false, "", null, Bing, Strings.Translate_Failed);
                string? detected = head.TryGetProperty("detectedLanguage", out var detectedNode) &&
                    detectedNode.ValueKind == JsonValueKind.Object &&
                    detectedNode.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                    ? language.GetString() : null;
                if (head.TryGetProperty("translations", out var translations) && translations.ValueKind == JsonValueKind.Array &&
                    translations.GetArrayLength() > 0 && translations[0].ValueKind == JsonValueKind.Object &&
                    translations[0].TryGetProperty("text", out var translated) && translated.ValueKind == JsonValueKind.String)
                {
                    var result = translated.GetString() ?? "";
                    if (result.Length > 0) return new(true, result, detected, Bing, null);
                }
            }
            return new(false, "", null, Bing, Strings.Translate_Failed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        {
            return new(false, "", null, Bing, error.Message);
        }
    }

    // params_AbusePreventionHelper = [ <毫秒时间戳> , "<token>" ]：Bing 页面里的一次性凭证。
    static (string Key, string Token)? ReadAbuseHelper(string page)
    {
        const string marker = "params_AbusePreventionHelper";
        for (var from = 0; ; )
        {
            var at = page.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) return null;
            var cursor = at + marker.Length;
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            if (cursor >= page.Length || page[cursor] != '=') { from = at + 1; continue; }
            cursor++;
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            if (cursor >= page.Length || page[cursor] != '[') { from = at + 1; continue; }
            cursor++;
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            var digits = cursor;
            while (cursor < page.Length && char.IsDigit(page[cursor])) cursor++;
            if (cursor == digits) { from = at + 1; continue; }
            var key = page[digits..cursor];
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            if (cursor >= page.Length || page[cursor] != ',') { from = at + 1; continue; }
            cursor++;
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            if (cursor >= page.Length || page[cursor] != '"') { from = at + 1; continue; }
            var closing = page.IndexOf('"', cursor + 1);
            if (closing <= cursor) { from = at + 1; continue; }
            return (key, page[(cursor + 1)..closing]);
        }
    }

    // 找 "标记<分隔符>\"值\""：同一标记可能出现多次，一次失败继续往后找（IG / data-iid 这类短词尤其容易重复）。
    static string? FindQuotedAfter(string page, string marker, char separator)
    {
        for (var from = 0; ; )
        {
            var at = page.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) return null;
            var cursor = at + marker.Length;
            while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
            if (cursor < page.Length && page[cursor] == separator)
            {
                cursor++;
                while (cursor < page.Length && char.IsWhiteSpace(page[cursor])) cursor++;
                if (cursor < page.Length && page[cursor] == '"')
                {
                    var closing = page.IndexOf('"', cursor + 1);
                    if (closing > cursor) return page[(cursor + 1)..closing];
                }
            }
            from = at + 1;
        }
    }

    // Pane 语言码 → 引擎码：zh 在必应是 zh-Hans、在谷歌是 zh-CN，其余一致。
    static string BingCode(string code) => code == "zh" ? "zh-Hans" : code;
    static string GoogleCode(string code) => code == "zh" ? "zh-CN" : code;
}
