using System.Net;
using System.Text;
using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure;
using Waio.Feature.Translate.Infrastructure.Providers;

namespace Waio.Feature.Translate.Tests;

/// <summary>免费源的解析、大模型流式返回(移植自 MAIO ProviderParsingTests / OpenAIStreamTests,用假的 HTTP 处理器)。</summary>
public class ProviderParsingTests
{
    [Fact]
    public void Google_整句()
    {
        var result = GoogleProvider.Parse(Encoding.UTF8.GetBytes("[[[\"你好世界\",\"hello world\",null,null,10]],null,\"en\"]"));

        Assert.Equal("你好世界", result.Text);
        Assert.Null(result.Dictionary);
    }

    [Fact]
    public void Google_查单词_音标和按词性分组的释义()
    {
        const string json = """
            [[["你好","hello",null,null,10],[null,null,"Nǐ hǎo","həˈlō"]],
             [["interjection",["你好!","喂!"],[]],["noun",["招呼"],[]]],"en"]
            """;
        var result = GoogleProvider.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal("你好", result.Text);
        Assert.Equal(["/həˈlō/"], result.Dictionary!.Phonetics);
        Assert.Equal(["interjection", "noun"], result.Dictionary.Meanings.Select(m => m.PartOfSpeech));
    }

    [Theory]
    [InlineData(429, "<html><head><title>Sorry...</title></head><body>unusual traffic</body></html>")]
    [InlineData(429, "")]
    [InlineData(503, "<html><head><meta charset=\"utf-8\"/><title>Sorry...</title></head></html>")]
    public void Google_被限流_显示友好提示而不是HTML(int status, string body)
    {
        var error = GoogleProvider.Failure(status, Encoding.UTF8.GetBytes(body));

        Assert.Equal(GoogleProvider.RateLimitedMessage, error.Message);
    }

    [Fact]
    public void Google_其他错误_保留状态码和内容()
    {
        var error = GoogleProvider.Failure(500, Encoding.UTF8.GetBytes("boom"));

        Assert.Equal("请求失败(HTTP 500):boom", error.Message);
    }

    [Fact]
    public void 必应_从网页里取凭证_提前五分钟过期()
    {
        const string page = """
            <div id="rich_tta" data-iid="translator.5023"></div>
            <script>_G={IG:"7B456D0E8E78409880A6D5829DAB4889",x:1};
            var params_AbusePreventionHelper = [1793662104109,"8HQLLI6f8J2t",3600000];</script>
            """;
        var now = DateTimeOffset.FromUnixTimeSeconds(0);
        var credentials = BingSession.Parse(page, "cn.bing.com", now)!;

        Assert.Equal("7B456D0E8E78409880A6D5829DAB4889", credentials.Ig);
        Assert.Equal("translator.5023", credentials.Iid);
        Assert.Equal("1793662104109", credentials.Key);
        Assert.Equal("8HQLLI6f8J2t", credentials.Token);
        Assert.Equal(now.AddSeconds(3300), credentials.ExpiresAt);
        Assert.Null(BingSession.Parse("<html>改版了</html>", "www.bing.com", now));
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    public void 大模型地址_可以填到v1_也可以填完整路径(string input, string expected)
    {
        Assert.Equal(expected, OpenAICompatibleProvider.Endpoint(input).ToString());
    }

    [Fact]
    public void 大模型_去掉包住译文的三引号()
    {
        Assert.Equal("你好", OpenAICompatibleProvider.StripQuotes("\"\"\"\n你好\n\"\"\""));
        Assert.Equal("你好", OpenAICompatibleProvider.StripQuotes("  你好 "));
    }

    [Fact]
    public void 地址格式不对_报错说明()
    {
        Assert.Throws<TranslationException>(() => Http.UserProvided("ftp://x"));
        Assert.Throws<TranslationException>(() => Http.UserProvided("   "));
    }

    // ---- 大模型流式 ----

    private static readonly TranslationRequest Hello = new("hello", Language.English, Language.SimplifiedChinese);

    private static (OpenAICompatibleProvider Provider, StubHandler Handler) Llm(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var provider = new OpenAICompatibleProvider("https://llm.test/v1", "test-model", "sk-test", PromptTemplate.Default, new Http(new HttpClient(handler)));
        return (provider, handler);
    }

    [Fact]
    public async Task 大模型_逐段输出_每段回调目前为止的完整译文()
    {
        var (provider, handler) = Llm(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"role":"assistant","content":""}}]}

            data: {"choices":[{"delta":{"content":"你"}}]}

            data: {"choices":[{"delta":{"content":"好"}}]}

            data: [DONE]

            """);
        var partials = new List<string>();

        var result = await provider.TranslateAsync(Hello, partials.Add, default);

        Assert.Equal("你好", result.Text);
        Assert.Equal(["", "你", "你好"], partials);
        Assert.Equal("https://llm.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.LastRequest.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task 大模型_服务端没按流式返回_按普通JSON解析()
    {
        var (provider, _) = Llm(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");

        Assert.Equal("你好", (await provider.TranslateAsync(Hello, _ => { }, default)).Text);
    }

    [Fact]
    public async Task 大模型_HTTP错误_带上状态码和返回内容()
    {
        var (provider, _) = Llm(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Authentication Fails\"}}");

        var error = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateAsync(Hello, _ => { }, default));

        Assert.Contains("401", error.Message);
        Assert.Contains("Authentication Fails", error.Message);
    }

    internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
