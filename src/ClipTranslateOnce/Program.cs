using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ClipTranslateOnce;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var cli = CliOptions.Parse(args);
            if (cli.ShowHelp)
            {
                Console.WriteLine(HelpText.Text);
                return 0;
            }

            if (cli.ShowVersion)
            {
                Console.WriteLine("ClipTranslateOnce 0.4.0");
                return 0;
            }

            var cfg = AppConfig.LoadOrCreate(cli.ConfigPath);
            cli.ApplyTo(cfg);

            string raw = AcquireText(cli, cfg.Input);
            raw = TextCleaner.Clean(raw, cfg.Input.MergePdfLineBreaks, cfg.Input.PreserveCodeLikeText);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Fail("没有读取到可翻译文本。", cfg, cli, 2);
                return 2;
            }

            if (raw.Length > cfg.Input.MaxChars)
            {
                raw = raw[..cfg.Input.MaxChars];
            }

            string translated;
            try
            {
                translated = Translator.TranslateAsync(raw, cfg.Translation).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                translated = "翻译失败：" + ex.Message + Environment.NewLine + Environment.NewLine + raw;
                if (!cli.NoOverlay)
                {
                    ShowOverlay(translated, cfg.Overlay);
                }
                Console.Error.WriteLine("翻译失败：" + ex.Message);
                return 3;
            }

            if (cli.Print)
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(translated);
            }

            if (!cli.NoOverlay)
            {
                ShowOverlay(translated, cfg.Overlay);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("执行失败：" + ex.Message);
            return 1;
        }
    }

    private static string AcquireText(CliOptions cli, InputConfig cfg)
    {
        if (cli.Text is not null) return cli.Text;

        if (cli.FilePath is not null)
        {
            var path = Environment.ExpandEnvironmentVariables(cli.FilePath);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        if (cli.ReadStdin)
        {
            if (!Console.IsInputRedirected)
                throw new InvalidOperationException("指定了 --stdin，但当前没有重定向/管道输入。建议使用：Get-Content -Raw input.txt | ClipTranslateOnce.exe --stdin");
            Console.InputEncoding = Encoding.UTF8;
            return Console.In.ReadToEnd();
        }

        if (cli.InputModeOverride is not null)
        {
            return TextInput.Acquire(cfg with { Mode = cli.InputModeOverride });
        }

        if (cli.PositionalText.Count > 0)
        {
            return string.Join(" ", cli.PositionalText);
        }

        return TextInput.Acquire(cfg);
    }

    private static void Fail(string message, AppConfig cfg, CliOptions cli, int exitCode)
    {
        Console.Error.WriteLine(message);
        if (!cli.NoOverlay)
        {
            ShowOverlay(message, cfg.Overlay);
        }
    }

    private static void ShowOverlay(string text, OverlayConfig overlay)
    {
        using var form = new OverlayForm(text, overlay);
        Application.Run(form);
    }
}

internal sealed class CliOptions
{
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool ReadStdin { get; private set; }
    public bool Print { get; private set; }
    public bool NoOverlay { get; private set; }
    public bool? MergePdfLineBreaks { get; private set; }
    public string? Text { get; private set; }
    public string? FilePath { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? InputModeOverride { get; private set; }
    public string? Provider { get; private set; }
    public string? TargetLang { get; private set; }
    public string? SourceLang { get; private set; }
    public int? MaxChars { get; private set; }
    public List<string> PositionalText { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var opt = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                    opt.ShowHelp = true;
                    break;
                case "--version":
                    opt.ShowVersion = true;
                    break;
                case "--stdin":
                    opt.ReadStdin = true;
                    break;
                case "--print":
                    opt.Print = true;
                    break;
                case "--no-overlay":
                    opt.NoOverlay = true;
                    break;
                case "--raw":
                    opt.MergePdfLineBreaks = false;
                    break;
                case "--merge-pdf-lines":
                    opt.MergePdfLineBreaks = true;
                    break;
                case "--clipboard":
                    opt.InputModeOverride = "clipboard";
                    break;
                case "--selected":
                    opt.InputModeOverride = "selected";
                    break;
                case "--auto":
                    opt.InputModeOverride = "auto";
                    break;
                case "--text":
                case "-t":
                    opt.Text = NeedValue(args, ref i, a);
                    break;
                case "--file":
                case "-f":
                    opt.FilePath = NeedValue(args, ref i, a);
                    break;
                case "--config":
                    opt.ConfigPath = NeedValue(args, ref i, a);
                    break;
                case "--provider":
                    opt.Provider = NeedValue(args, ref i, a);
                    break;
                case "--target":
                    opt.TargetLang = NeedValue(args, ref i, a);
                    break;
                case "--source":
                    opt.SourceLang = NeedValue(args, ref i, a);
                    break;
                case "--max-chars":
                    if (int.TryParse(NeedValue(args, ref i, a), out var n)) opt.MaxChars = n;
                    else throw new ArgumentException("--max-chars 必须是整数。");
                    break;
                default:
                    if (a.StartsWith("--text=", StringComparison.Ordinal)) opt.Text = a[7..];
                    else if (a.StartsWith("--file=", StringComparison.Ordinal)) opt.FilePath = a[7..];
                    else if (a.StartsWith("--config=", StringComparison.Ordinal)) opt.ConfigPath = a[9..];
                    else if (a.StartsWith("--provider=", StringComparison.Ordinal)) opt.Provider = a[11..];
                    else if (a.StartsWith("--target=", StringComparison.Ordinal)) opt.TargetLang = a[9..];
                    else if (a.StartsWith("--source=", StringComparison.Ordinal)) opt.SourceLang = a[9..];
                    else if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("未知参数：" + a);
                    else opt.PositionalText.Add(a);
                    break;
            }
        }

        var explicitInputs = 0;
        if (opt.Text is not null) explicitInputs++;
        if (opt.FilePath is not null) explicitInputs++;
        if (opt.ReadStdin) explicitInputs++;
        if (opt.InputModeOverride is not null) explicitInputs++;
        if (opt.PositionalText.Count > 0) explicitInputs++;
        if (explicitInputs > 1)
            throw new ArgumentException("输入来源不能同时指定多个。请选择 --stdin、--file、--text、--clipboard、--selected、--auto 或位置参数中的一种。");

        if (opt.NoOverlay) opt.Print = true;

        return opt;
    }

    public void ApplyTo(AppConfig cfg)
    {
        if (Provider is not null) cfg.Translation.Provider = Provider;
        if (TargetLang is not null) cfg.Translation.TargetLang = TargetLang;
        if (SourceLang is not null) cfg.Translation.SourceLang = SourceLang;
        if (MaxChars is not null) cfg.Input.MaxChars = Math.Max(1, MaxChars.Value);
        if (MergePdfLineBreaks is not null) cfg.Input.MergePdfLineBreaks = MergePdfLineBreaks.Value;
    }

    private static string NeedValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{name} 缺少参数值。");
        return args[++i];
    }
}

internal static class HelpText
{
    public const string Text = """
ClipTranslateOnce - 单次调用的低资源浮层翻译工具

用法：
  ClipTranslateOnce.exe [选项] [文本]

推荐输入方式：
  ClipTranslateOnce.exe --clipboard
  ClipTranslateOnce.exe --file input.txt
  Get-Content -Raw input.txt | ClipTranslateOnce.exe --stdin
  "This is a test." | ClipTranslateOnce.exe --stdin

输入选项：
  --stdin                 从标准输入读取。最适合长文本、多行文本、特殊字符文本。
  -f, --file <path>       从 UTF-8 文本文件读取。
  -t, --text <text>       从一个命令行参数读取文本。短文本可用，长文本不推荐。
  --clipboard             直接读取剪切板文本。
  --selected              模拟 Ctrl+C 读取当前选中文本，然后尽量恢复剪切板。
  --auto                  先尝试 selected，失败后读取 clipboard。

翻译选项：
  --provider <name>       keyless | mymemory | youdao-mobile | sogou-page | bing-web | baidu-web | iciba-web | microsoft | baidu | libretranslate | deepl | echo
  --target <lang>         目标语言，例如 ZH-HANS、EN-US、JA、zh、en。
  --source <lang>         源语言。留空时由翻译后端自动检测。
  --max-chars <n>         最大输入字符数。

输出选项：
  --print                 同时把译文输出到控制台。
  --no-overlay            不显示浮层，只配合 --print 使用。
  --raw                   不合并 PDF 复制产生的断行。
  --merge-pdf-lines       合并 PDF 复制产生的断行。
  --config <path>         指定配置文件路径。
  -h, --help              显示帮助。
  --version               显示版本。

说明：
  对长文本、多行文本、含引号/括号/换行/特殊符号的文本，优先使用 --stdin 或 --file，避免命令行转义问题。
""";
}

internal sealed record AppConfig
{
    public InputConfig Input { get; set; } = new();
    public TranslationConfig Translation { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    public static AppConfig LoadOrCreate(string? explicitPath)
    {
        var path = ResolveConfigPath(explicitPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            var cfg = new AppConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, options), new UTF8Encoding(false));
            return cfg;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
    }

    private static string ResolveConfigPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath));

        var local = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(local)) return local;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ClipTranslateOnce", "appsettings.json");
    }
}

internal sealed record InputConfig
{
    // selected: 模拟 Ctrl+C 读取当前选区；clipboard: 直接读取剪切板；auto: 先 selected，失败再 clipboard。
    public string Mode { get; set; } = "clipboard";
    public int CopyDelayMs { get; set; } = 160;
    public int CopyTimeoutMs { get; set; } = 900;
    public bool RestoreClipboard { get; set; } = true;
    public int MaxChars { get; set; } = 5000;
    public bool MergePdfLineBreaks { get; set; } = true;
    public bool PreserveCodeLikeText { get; set; } = true;
}

internal sealed record TranslationConfig
{
    // keyless | mymemory | youdao-mobile | sogou-page | bing-web | baidu-web | iciba-web | microsoft | baidu | libretranslate | deepl | echo
    public string Provider { get; set; } = "keyless";
    public string TargetLang { get; set; } = "ZH-HANS";
    public string AlternateTargetLangWhenInputLooksChinese { get; set; } = "EN-US";
    public string SourceLang { get; set; } = "";
    public int TimeoutMs { get; set; } = 8000;

    // 无需手动申请密钥的实验性后端。它们主要来自公共网页接口或页面解析，稳定性弱于正式 API。
    public string KeylessFallbackProviders { get; set; } = "mymemory,youdao-mobile,bing-web,sogou-page,iciba-web";
    public string WebUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";
    public string YoudaoMobileEndpoint { get; set; } = "http://m.youdao.com/translate";
    public string SogouPageEndpoint { get; set; } = "https://fanyi.sogou.com/text";
    public string BingWebBaseUrl { get; set; } = "https://cn.bing.com";
    public string BaiduWebBaseUrl { get; set; } = "https://fanyi.baidu.com";
    public string IcibaWebEndpointPrefix { get; set; } = "https://ifanyi.iciba.com/index.php?c=trans&m=fy&client=6&auth_user=key_web_fanyi&sign=";
    public string IcibaWebSecretPrefix { get; set; } = "6key_web_fanyiifanyiweb8hc9s98e";

    // MyMemory：可无密钥使用；sourceLang 为空时会按文本粗略判断源语言。
    public string MyMemoryEndpoint { get; set; } = "https://api.mymemory.translated.net/get";
    public string MyMemoryEmail { get; set; } = "";
    public string MyMemoryKey { get; set; } = "";

    // Microsoft Translator / Azure AI Translator。
    public string MicrosoftEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";
    public string MicrosoftApiKeyEnv { get; set; } = "AZURE_TRANSLATOR_KEY";
    public string MicrosoftApiKey { get; set; } = "";
    public string MicrosoftRegionEnv { get; set; } = "AZURE_TRANSLATOR_REGION";
    public string MicrosoftRegion { get; set; } = "";

    // 百度翻译开放平台。
    public string BaiduEndpoint { get; set; } = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    public string BaiduAppIdEnv { get; set; } = "BAIDU_TRANSLATE_APPID";
    public string BaiduAppId { get; set; } = "";
    public string BaiduKeyEnv { get; set; } = "BAIDU_TRANSLATE_KEY";
    public string BaiduKey { get; set; } = "";

    // DeepL：保留兼容。
    public string ApiKeyEnv { get; set; } = "DEEPL_API_KEY";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://api-free.deepl.com/v2/translate";
    public string DeeplModelType { get; set; } = "latency_optimized";
    public string DeeplSplitSentences { get; set; } = "nonewlines";

    // LibreTranslate：自建实例通常不需要 key，公网实例可能需要 key。
    public string LibreTranslateEndpoint { get; set; } = "http://127.0.0.1:5000/translate";
    public string LibreTranslateApiKey { get; set; } = "";
}

internal sealed record OverlayConfig
{
    public int MaxWidthPx { get; set; } = 620;
    public int MaxHeightPx { get; set; } = 520;
    public int MinWidthPx { get; set; } = 220;
    public int PaddingPx { get; set; } = 14;
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 18;
    public double Opacity { get; set; } = 0.96;
    public string FontName { get; set; } = "Microsoft YaHei UI";
    public float FontSize { get; set; } = 10.5f;
    public bool CopyOnRightClick { get; set; } = true;
}

internal static class TextInput
{
    public static string Acquire(InputConfig cfg)
    {
        var mode = (cfg.Mode ?? "clipboard").Trim().ToLowerInvariant();
        return mode switch
        {
            "selected" => TryGetSelectedText(cfg) ?? "",
            "auto" => TryGetSelectedText(cfg) is { Length: > 0 } s ? s : GetClipboardText(),
            _ => GetClipboardText()
        };
    }

    private static string? TryGetSelectedText(InputConfig cfg)
    {
        IDataObject? oldClipboard = null;
        bool hadClipboard = false;
        uint beforeSeq = NativeMethods.GetClipboardSequenceNumber();

        if (cfg.RestoreClipboard)
        {
            try
            {
                oldClipboard = Clipboard.GetDataObject();
                hadClipboard = oldClipboard is not null;
            }
            catch
            {
                // Clipboard may be locked by another process.
            }
        }

        NativeMethods.SendCtrlC();
        Thread.Sleep(Math.Max(0, cfg.CopyDelayMs));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < cfg.CopyTimeoutMs)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != beforeSeq) break;
            Thread.Sleep(25);
        }

        string text = GetClipboardText();

        if (cfg.RestoreClipboard)
        {
            try
            {
                if (hadClipboard && oldClipboard is not null)
                    Clipboard.SetDataObject(oldClipboard, true);
                else
                    Clipboard.Clear();
            }
            catch
            {
                // Do not fail translation only because clipboard restoration failed.
            }
        }

        return text;
    }

    private static string GetClipboardText()
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : "";
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        return "";
    }
}

internal static class TextCleaner
{
    public static string Clean(string text, bool mergePdfLineBreaks, bool preserveCodeLikeText)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var s = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (!mergePdfLineBreaks) return s;
        if (preserveCodeLikeText && LooksCodeLike(s)) return s;

        var paragraphs = Regex.Split(s, "\\n{2,}")
            .Select(MergeSingleParagraph)
            .Where(p => !string.IsNullOrWhiteSpace(p));

        return string.Join("\n\n", paragraphs).Trim();
    }

    private static string MergeSingleParagraph(string para)
    {
        var lines = para.Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        if (lines.Length <= 1) return para.Trim();

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }

            if (Regex.IsMatch(line, @"^([-*•]|\d+[.)])\s+"))
            {
                sb.Append('\n').Append(line);
            }
            else if (sb[^1] == '-')
            {
                sb.Length -= 1;
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
        }

        return sb.ToString();
    }

    private static bool LooksCodeLike(string s)
    {
        var lines = s.Split('\n');
        if (lines.Length < 3) return false;

        int codeSignals = 0;
        foreach (var line in lines.Take(30))
        {
            var t = line.Trim();
            if (t.StartsWith("using ") || t.StartsWith("namespace ") || t.StartsWith("public ") || t.StartsWith("private ")) codeSignals++;
            if (Regex.IsMatch(t, @"[{};=<>]|^#include|^import\s|^def\s|^class\s|^function\s|^var\s|^let\s|^const\s")) codeSignals++;
            if (Regex.IsMatch(line, @"^\s{2,}\S")) codeSignals++;
        }

        return codeSignals >= 4;
    }
}

internal static class Translator
{
    public static Task<string> TranslateAsync(string text, TranslationConfig cfg)
    {
        var provider = (cfg.Provider ?? "mymemory").Trim().ToLowerInvariant();
        return provider switch
        {
            "echo" => Task.FromResult(text),
            "keyless" or "auto-keyless" or "webauto" => TranslateWithKeylessAutoAsync(text, cfg),
            "mymemory" => TranslateWithMyMemoryAsync(text, cfg),
            "youdao" or "youdao-mobile" => TranslateWithYoudaoMobileAsync(text, cfg),
            "sogou" or "sogou-page" => TranslateWithSogouPageAsync(text, cfg),
            "bing-web" => TranslateWithBingWebAsync(text, cfg),
            "baidu-web" => TranslateWithBaiduWebAsync(text, cfg),
            "iciba" or "iciba-web" => TranslateWithIcibaWebAsync(text, cfg),
            "microsoft" or "azure" or "azuretranslator" => TranslateWithMicrosoftAsync(text, cfg),
            "baidu" or "baidu-api" => TranslateWithBaiduAsync(text, cfg),
            "libretranslate" => TranslateWithLibreAsync(text, cfg),
            "deepl" => TranslateWithDeepLAsync(text, cfg),
            _ => throw new InvalidOperationException($"未知翻译后端：{cfg.Provider}。可用值：keyless、mymemory、youdao-mobile、sogou-page、bing-web、baidu-web、iciba-web、microsoft、baidu、libretranslate、deepl、echo。")
        };
    }

    private static async Task<string> TranslateWithKeylessAutoAsync(string text, TranslationConfig cfg)
    {
        var providers = (cfg.KeylessFallbackProviders ?? "mymemory,youdao-mobile,bing-web,sogou-page,iciba-web")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals("keyless", StringComparison.OrdinalIgnoreCase)
                     && !p.Equals("auto-keyless", StringComparison.OrdinalIgnoreCase)
                     && !p.Equals("webauto", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (providers.Length == 0)
            throw new InvalidOperationException("keyless 没有可用的 fallback provider。请配置 translation.keylessFallbackProviders。 ");

        var errors = new List<string>();
        foreach (var p in providers)
        {
            try
            {
                var result = await TranslateAsync(text, cfg with { Provider = p }).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result)) return result;
                errors.Add($"{p}: 返回空译文");
            }
            catch (Exception ex)
            {
                errors.Add($"{p}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("keyless fallback 全部失败：" + string.Join(" | ", errors.Select(TrimError)));
    }

    private static async Task<string> TranslateWithYoudaoMobileAsync(string text, TranslationConfig cfg)
    {
        if (text.Length > 1000)
            throw new InvalidOperationException("youdao-mobile 通常只适合 1000 字符以内文本。长文本请改用 mymemory、bing-web、sogou-page 或正式 API。 ");

        using var http = CreateBrowserHttpClient(cfg);
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.YoudaoMobileEndpoint);
        req.Headers.Referrer = new Uri("http://m.youdao.com/");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"] = "AUTO",
            ["inputtext"] = text
        });

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Youdao mobile HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        var m = Regex.Match(body, @"id\s*=\s*['""']translateResult['""'][\s\S]*?<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(body, @"<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase);

        var translated = m.Success ? HtmlToText(m.Groups[1].Value) : "";
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("Youdao mobile 返回空译文或页面结构已变化。 ");
        return translated;
    }

    private static async Task<string> TranslateWithSogouPageAsync(string text, TranslationConfig cfg)
    {
        var target = NormalizeSogouWebLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var endpoint = cfg.SogouPageEndpoint;
        var query = new Dictionary<string, string>
        {
            ["keyword"] = text,
            ["transto"] = target
        };

        using var http = CreateBrowserHttpClient(cfg);
        using var resp = await http.GetAsync(AppendQuery(endpoint, query)).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sogou page HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        var m = Regex.Match(body, @"id\s*=\s*['""']trans-result['""'][^>]*>([\s\S]*?)</", RegexOptions.IgnoreCase);
        var translated = m.Success ? HtmlToText(m.Groups[1].Value) : "";
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("Sogou page 返回空译文或页面结构已变化。 ");
        return translated;
    }

    private static async Task<string> TranslateWithBingWebAsync(string text, TranslationConfig cfg)
    {
        if (text.Length > 2500)
            throw new InvalidOperationException("bing-web 通常只适合 2500 字符以内文本。长文本请分段或改用正式 API。 ");

        var baseUrl = cfg.BingWebBaseUrl.TrimEnd('/');
        var target = NormalizeBingWebLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));

        using var http = CreateBrowserHttpClient(cfg);
        var html = await http.GetStringAsync(baseUrl + "/translator").ConfigureAwait(false);

        var ig = RegexMatch(html, "IG", @"IG:\s*['""']([A-Za-z0-9]+)['""']");
        var abuse = Regex.Match(html, @"var\s+params_AbusePreventionHelper\s*=\s*\[\s*([0-9]+)\s*,\s*['""']([^'""']+)['""']", RegexOptions.IgnoreCase);
        if (!abuse.Success)
            throw new InvalidOperationException("Bing web 未找到 AbusePreventionHelper token，页面结构可能已变化。 ");
        var key = abuse.Groups[1].Value;
        var token = abuse.Groups[2].Value;
        var iidMatch = Regex.Match(html, @"data-iid\s*=\s*['""']([^'""']+)['""']", RegexOptions.IgnoreCase);
        var iid = iidMatch.Success ? iidMatch.Groups[1].Value : "translator.5028";

        var url = $"{baseUrl}/ttranslatev3?isVertical=1&&IG={Uri.EscapeDataString(ig)}&IID={Uri.EscapeDataString(iid)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Referrer = new Uri(baseUrl + "/translator");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = string.IsNullOrWhiteSpace(cfg.SourceLang) ? "auto-detect" : NormalizeBingWebLang(cfg.SourceLang),
            ["to"] = target,
            ["token"] = token,
            ["key"] = key,
            ["text"] = text
        });

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bing web HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        var translated = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("Bing web 返回空译文。 ");
        return translated;
    }

    private static async Task<string> TranslateWithBaiduWebAsync(string text, TranslationConfig cfg)
    {
        var baseUrl = cfg.BaiduWebBaseUrl.TrimEnd('/');
        var target = NormalizeBaiduWebLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));

        using var http = CreateBrowserHttpClient(cfg);
        var html = await http.GetStringAsync(baseUrl).ConfigureAwait(false);
        var gtk = RegexMatch(html, "Baidu gtk", @"window\.gtk\s*=\s*['""']([^'""']+)['""']");
        var token = RegexMatch(html, "Baidu token", @"token:\s*['""']([^'""']+)['""']");

        var signText = text.Length > 30
            ? SafeSlice(text, 0, 10) + SafeSlice(text, text.Length / 2 - 5, 10) + SafeSlice(text, text.Length - 10, 10)
            : text;
        var sign = BaiduWebTk(signText, gtk);

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v2transapi");
        req.Headers.Referrer = new Uri(baseUrl + "/");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = string.IsNullOrWhiteSpace(cfg.SourceLang) ? "auto" : NormalizeBaiduWebLang(cfg.SourceLang),
            ["to"] = target,
            ["query"] = text,
            ["simple_means_flag"] = "3",
            ["sign"] = sign,
            ["token"] = token,
            ["domain"] = "common"
        });

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Baidu web HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("errno", out var errno) && errno.ValueKind == JsonValueKind.Number && errno.GetInt32() != 0)
            throw new InvalidOperationException("Baidu web errno=" + errno.GetInt32() + ": " + TrimError(body));

        if (!root.TryGetProperty("trans_result", out var tr) || !tr.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Baidu web 返回中没有 trans_result.data：" + TrimError(body));

        var translated = string.Join(Environment.NewLine,
            data.EnumerateArray()
                .Select(x => x.TryGetProperty("dst", out var dst) ? dst.GetString() : "")
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("Baidu web 返回空译文。 ");
        return translated;
    }

    private static async Task<string> TranslateWithIcibaWebAsync(string text, TranslationConfig cfg)
    {
        var target = NormalizeIcibaWebLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var sign = Md5Hex(cfg.IcibaWebSecretPrefix + text.Trim()).Substring(0, 16);

        using var http = CreateBrowserHttpClient(cfg);
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.IcibaWebEndpointPrefix + sign);
        req.Headers.Referrer = new Uri("https://ifanyi.iciba.com/");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = string.IsNullOrWhiteSpace(cfg.SourceLang) ? "auto" : NormalizeIcibaWebLang(cfg.SourceLang),
            ["t"] = target,
            ["q"] = text
        });

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Iciba web HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var content) || !content.TryGetProperty("out", out var outText))
            throw new InvalidOperationException("Iciba web 返回中没有 content.out：" + TrimError(body));

        var translated = outText.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("Iciba web 返回空译文。 ");
        return translated;
    }

    private static async Task<string> TranslateWithMyMemoryAsync(string text, TranslationConfig cfg)
    {
        var target = NormalizeMyMemoryLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var source = string.IsNullOrWhiteSpace(cfg.SourceLang)
            ? (LooksChinese(text) ? "zh-CN" : "en")
            : NormalizeMyMemoryLang(cfg.SourceLang);

        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            target = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-CN";

        var query = new Dictionary<string, string>
        {
            ["q"] = text,
            ["langpair"] = $"{source}|{target}",
            ["mt"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(cfg.MyMemoryEmail)) query["de"] = cfg.MyMemoryEmail.Trim();
        if (!string.IsNullOrWhiteSpace(cfg.MyMemoryKey)) query["key"] = cfg.MyMemoryKey.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        using var resp = await http.GetAsync(AppendQuery(cfg.MyMemoryEndpoint, query)).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MyMemory HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("responseStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() >= 400)
        {
            var details = root.TryGetProperty("responseDetails", out var d) ? d.GetString() : body;
            throw new InvalidOperationException("MyMemory: " + TrimError(details ?? body));
        }

        var translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        translated = System.Net.WebUtility.HtmlDecode(translated).Trim();
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("MyMemory 返回空译文。若源语言自动判断不准，请显式指定 --source en / --source zh-CN / --source ja。 ");
        return translated;
    }

    private static async Task<string> TranslateWithMicrosoftAsync(string text, TranslationConfig cfg)
    {
        var apiKey = ReadSecret(cfg.MicrosoftApiKey, cfg.MicrosoftApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"没有配置 Microsoft Translator key。请设置环境变量 {cfg.MicrosoftApiKeyEnv}，或在 appsettings.json 的 translation.microsoftApiKey 中填写。调试时可把 provider 改为 mymemory 或 echo。 ");

        var target = NormalizeMicrosoftLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var query = new Dictionary<string, string> { ["to"] = target };
        if (!string.IsNullOrWhiteSpace(cfg.SourceLang)) query["from"] = NormalizeMicrosoftLang(cfg.SourceLang);

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        using var req = new HttpRequestMessage(HttpMethod.Post, AppendQuery(cfg.MicrosoftEndpoint, query));
        req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);

        var region = ReadSecret(cfg.MicrosoftRegion, cfg.MicrosoftRegionEnv);
        if (!string.IsNullOrWhiteSpace(region)) req.Headers.Add("Ocp-Apim-Subscription-Region", region.Trim());
        req.Headers.Add("X-ClientTraceId", Guid.NewGuid().ToString());
        req.Content = new StringContent(JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Translator HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> TranslateWithBaiduAsync(string text, TranslationConfig cfg)
    {
        var appId = ReadSecret(cfg.BaiduAppId, cfg.BaiduAppIdEnv);
        var key = ReadSecret(cfg.BaiduKey, cfg.BaiduKeyEnv);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"没有配置百度翻译 APPID/Key。请设置环境变量 {cfg.BaiduAppIdEnv} 和 {cfg.BaiduKeyEnv}，或在 appsettings.json 中填写 translation.baiduAppId / translation.baiduKey。调试时可把 provider 改为 mymemory 或 echo。 ");

        var source = string.IsNullOrWhiteSpace(cfg.SourceLang) ? "auto" : NormalizeBaiduLang(cfg.SourceLang);
        var target = NormalizeBaiduLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var salt = Random.Shared.Next(100000, 999999999).ToString();
        var sign = Md5Hex(appId.Trim() + text + salt + key.Trim());

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = source,
            ["to"] = target,
            ["appid"] = appId.Trim(),
            ["salt"] = salt,
            ["sign"] = sign
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(cfg.BaiduEndpoint, content).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Baidu HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error_code", out var errorCode))
        {
            var msg = root.TryGetProperty("error_msg", out var errorMsg) ? errorMsg.GetString() : body;
            throw new InvalidOperationException($"Baidu {errorCode.GetString()}: {TrimError(msg ?? body)}");
        }

        if (!root.TryGetProperty("trans_result", out var result) || result.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Baidu 返回中没有 trans_result：" + TrimError(body));

        return string.Join(Environment.NewLine,
            result.EnumerateArray()
                  .Select(x => x.TryGetProperty("dst", out var dst) ? dst.GetString() : "")
                  .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static async Task<string> TranslateWithDeepLAsync(string text, TranslationConfig cfg)
    {
        var apiKey = ReadSecret(cfg.ApiKey, cfg.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"没有配置 DeepL API key。请设置环境变量 {cfg.ApiKeyEnv}，或在 appsettings.json 的 translation.apiKey 中填写。调试时可把 provider 改为 mymemory 或 echo。 ");

        var target = PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = new[] { text },
            ["target_lang"] = target
        };
        if (!string.IsNullOrWhiteSpace(cfg.SourceLang)) payload["source_lang"] = cfg.SourceLang.Trim();
        if (!string.IsNullOrWhiteSpace(cfg.DeeplModelType)) payload["model_type"] = cfg.DeeplModelType.Trim();
        if (!string.IsNullOrWhiteSpace(cfg.DeeplSplitSentences)) payload["split_sentences"] = cfg.DeeplSplitSentences.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> TranslateWithLibreAsync(string text, TranslationConfig cfg)
    {
        var target = NormalizeLibreLang(PickTargetLanguage(text, cfg.TargetLang, cfg.AlternateTargetLangWhenInputLooksChinese));
        var source = string.IsNullOrWhiteSpace(cfg.SourceLang) ? "auto" : NormalizeLibreLang(cfg.SourceLang);
        var payload = new Dictionary<string, object?>
        {
            ["q"] = text,
            ["source"] = source,
            ["target"] = target,
            ["format"] = "text"
        };
        if (!string.IsNullOrWhiteSpace(cfg.LibreTranslateApiKey)) payload["api_key"] = cfg.LibreTranslateApiKey.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(cfg.LibreTranslateEndpoint, content).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LibreTranslate HTTP {(int)resp.StatusCode}: {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("translatedText").GetString() ?? "";
    }

    private static string ReadSecret(string directValue, string envName)
    {
        if (!string.IsNullOrWhiteSpace(directValue)) return directValue.Trim();
        return string.IsNullOrWhiteSpace(envName) ? "" : (Environment.GetEnvironmentVariable(envName.Trim()) ?? "").Trim();
    }

    private static string PickTargetLanguage(string text, string target, string alternateWhenChinese)
    {
        target = string.IsNullOrWhiteSpace(target) ? "ZH-HANS" : target.Trim();
        if ((target.StartsWith("ZH", StringComparison.OrdinalIgnoreCase) || target.Equals("zh", StringComparison.OrdinalIgnoreCase)) && LooksChinese(text))
            return string.IsNullOrWhiteSpace(alternateWhenChinese) ? "EN-US" : alternateWhenChinese.Trim();
        return target;
    }

    private static HttpClient CreateBrowserHttpClient(TranslationConfig cfg)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(cfg.WebUserAgent) ? "Mozilla/5.0" : cfg.WebUserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return http;
    }

    private static string RegexMatch(string input, string name, string pattern)
    {
        var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new InvalidOperationException($"未找到 {name}，目标页面结构可能已变化。");
        return m.Groups[1].Value;
    }

    private static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<.*?>", "", RegexOptions.Singleline);
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    private static string SafeSlice(string s, int start, int length)
    {
        if (string.IsNullOrEmpty(s) || length <= 0) return "";
        start = Math.Clamp(start, 0, s.Length);
        length = Math.Clamp(length, 0, s.Length - start);
        return s.Substring(start, length);
    }

    private static string BaiduWebTk(string text, string gtk)
    {
        var parts = gtk.Split('.');
        if (parts.Length < 2 || !long.TryParse(parts[0], out var b) || !long.TryParse(parts[1], out var suffix))
            throw new InvalidOperationException("Baidu gtk 格式异常：" + gtk);

        var a = b;
        foreach (var by in Encoding.UTF8.GetBytes(text))
            a = BaiduFo(a + by, "+-a^+6");
        a = BaiduFo(a, "+-3^+b+-f");
        a ^= suffix;
        if (a < 0) a = (a & 2147483647L) + 2147483648L;
        a %= 1000000L;
        return a + "." + (a ^ b);
    }

    private static long BaiduFo(long a, string b)
    {
        for (int c = 0; c < b.Length - 2; c += 3)
        {
            var dChar = b[c + 2];
            var d = dChar >= 'a' ? dChar - 87 : dChar - '0';
            var shifted = b[c + 1] == '+' ? (long)((uint)a >> d) : (a << d);
            a = b[c] == '+' ? (a + shifted) & 4294967295L : a ^ shifted;
        }
        return a;
    }

    private static string NormalizeSogouWebLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-CHS",
            "zh-tw" or "zh-hant" or "cht" => "zh-CHT",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            "kr" => "ko",
            _ => lang
        };
    }

    private static string NormalizeBingWebLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-Hans",
            "zh-tw" or "zh-hant" or "cht" => "zh-Hant",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            _ => lang
        };
    }

    private static string NormalizeBaiduWebLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" or "cht" => "cht",
            "en-us" or "en-gb" => "en",
            "ja" => "jp",
            "ko" => "kor",
            "fr" => "fra",
            "es" => "spa",
            _ => lang
        };
    }

    private static string NormalizeIcibaWebLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" or "cht" => "cht",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            _ => lang
        };
    }

    private static string NormalizeMicrosoftLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-Hans",
            "zh-tw" or "zh-hant" => "zh-Hant",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            _ => lang
        };
    }

    private static string NormalizeBaiduLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" or "cht" => "cht",
            "en-us" or "en-gb" => "en",
            "ja" => "jp",
            _ => lang
        };
    }

    private static string NormalizeMyMemoryLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-CN",
            "zh-tw" or "zh-hant" or "cht" => "zh-TW",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            _ => lang
        };
    }

    private static string NormalizeLibreLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "zh-hans" or "zh-hant" or "zh-cn" or "zh-tw" => "zh",
            "en-us" or "en-gb" => "en",
            "jp" => "ja",
            _ => lang
        };
    }

    private static bool LooksChinese(string text)
    {
        int cjk = 0, letters = 0;
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff') cjk++;
            if (char.IsLetter(ch)) letters++;
        }
        return letters > 0 && cjk >= Math.Max(2, letters / 4);
    }

    private static string AppendQuery(string endpoint, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(endpoint);
        sb.Append(endpoint.Contains('?') ? '&' : '?');
        sb.Append(string.Join("&", values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));
        return sb.ToString();
    }

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string TrimError(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Length <= 600 ? s : s[..600] + "...";
}

internal sealed class OverlayForm : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly string _text;
    private readonly OverlayConfig _cfg;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public OverlayForm(string text, OverlayConfig cfg)
    {
        _text = text;
        _cfg = cfg;
        ConfigureWindow();
        BuildContent();
    }

    private void ConfigureWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = Math.Clamp(_cfg.Opacity, 0.55, 1.0);
        BackColor = Color.FromArgb(250, 250, 250);
        Padding = new Padding(_cfg.PaddingPx);
    }

    private void BuildContent()
    {
        var font = new Font(_cfg.FontName, _cfg.FontSize, FontStyle.Regular, GraphicsUnit.Point);
        int closeSize = 24;
        int textMaxWidth = Math.Max(_cfg.MinWidthPx, _cfg.MaxWidthPx - _cfg.PaddingPx * 2);

        var measured = TextRenderer.MeasureText(
            _text,
            font,
            new Size(textMaxWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        int contentWidth = Math.Clamp(measured.Width + 10, _cfg.MinWidthPx, _cfg.MaxWidthPx);
        int contentHeight = Math.Min(measured.Height + 8, _cfg.MaxHeightPx - closeSize - _cfg.PaddingPx * 2);
        int formWidth = contentWidth + _cfg.PaddingPx * 2;
        int formHeight = contentHeight + closeSize + _cfg.PaddingPx * 2;
        Size = new Size(formWidth, formHeight);

        var close = new Label
        {
            Text = "×",
            Font = new Font(_cfg.FontName, 12f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(formWidth - _cfg.PaddingPx - closeSize, _cfg.PaddingPx - 4, closeSize, closeSize)
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);

        var panel = new Panel
        {
            AutoScroll = measured.Height + 8 > contentHeight,
            Bounds = new Rectangle(_cfg.PaddingPx, _cfg.PaddingPx + closeSize, contentWidth, contentHeight),
            BackColor = Color.Transparent
        };

        var label = new Label
        {
            Text = _text,
            Font = font,
            AutoSize = true,
            MaximumSize = new Size(contentWidth - 6, 0),
            BackColor = Color.Transparent
        };

        if (_cfg.CopyOnRightClick)
        {
            label.MouseUp += (_, e) => CopyOnRightClick(e);
            panel.MouseUp += (_, e) => CopyOnRightClick(e);
        }

        panel.Controls.Add(label);
        Controls.Add(panel);
        PositionNearCursor();
    }

    private void CopyOnRightClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        try { Clipboard.SetText(_text); } catch { }
        Close();
    }

    private void PositionNearCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        var screen = Screen.FromPoint(new Point(p.X, p.Y)).WorkingArea;

        int x = p.X + _cfg.OffsetX;
        int y = p.Y + _cfg.OffsetY;

        if (x + Width > screen.Right) x = p.X - Width - _cfg.OffsetX;
        if (y + Height > screen.Bottom) y = p.Y - Height - _cfg.OffsetY;
        if (x < screen.Left) x = screen.Left;
        if (y < screen.Top) y = screen.Top;

        Location = new Point(x, y);
    }
}

internal static class NativeMethods
{
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public static void SendCtrlC()
    {
        var inputs = new[]
        {
            KeyDown(VK_CONTROL),
            KeyDown(VK_C),
            KeyUp(VK_C),
            KeyUp(VK_CONTROL)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyDown(ushort vk) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Ki = new KEYBDINPUT { WVk = vk } }
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Ki = new KEYBDINPUT { WVk = vk, DwFlags = KEYEVENTF_KEYUP } }
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}
