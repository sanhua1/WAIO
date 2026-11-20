using System.Text;
using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure;
using Waio.Feature.Translate.Infrastructure.Providers;

namespace Waio.Feature.Translate.Tests;

/// <summary>签名与语言代码(移植自 MAIO KeyedProviderTests,期望值相同:官方文档示例或按官方步骤独立算出)。</summary>
public class KeyedProviderTests
{
    private static readonly Http NoNetwork = new(new HttpClient(new ThrowingHandler()));

    [Fact]
    public void 百度签名_与官方文档示例一致()
    {
        Assert.Equal("f89f9594663708c1605f3d736d01d2d4", BaiduProvider.Sign("2015063000000001", "apple", "1435660288", "12345678"));
    }

    [Fact]
    public void 有道_长文本截断规则()
    {
        Assert.Equal("short text", YoudaoProvider.Truncate("short text"));
        Assert.Equal("Hello, thi42or youdao.", YoudaoProvider.Truncate("Hello, this is a long sentence for youdao."));
        Assert.Equal("一二三四五六七八九十21乙丙丁戊己庚辛壬癸子", YoudaoProvider.Truncate("一二三四五六七八九十甲乙丙丁戊己庚辛壬癸子"));
    }

    [Fact]
    public void 有道签名V3()
    {
        Assert.Equal(
            "48598b4f1e80e1f36d46fdac7ebd7b362f8da3288e588ff71e2c1492442c261b",
            YoudaoProvider.Sign("appk", "Hello, this is a long sentence for youdao.", "salt1", "1700000000", "secr"));
    }

    [Fact]
    public void 腾讯云_规范请求与签名_与官方示例一致()
    {
        const string payload = "{\"Limit\": 1, \"Filters\": [{\"Values\": [\"\\u672a\\u547d\\u540d\"], \"Name\": \"instance-name\"}]}";
        var signing = new TencentSigning("cvm.tencentcloudapi.com", "cvm", "DescribeInstances", "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(payload), 1_551_113_065);

        Assert.Equal("2019-02-25", signing.Date);
        Assert.Equal("35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064", Crypto.Sha256Hex(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal("7019a55be8395899b900fb5564e4200d984910f34794a27cb3fb7d10ff6a1e84", Crypto.Sha256Hex(signing.CanonicalRequest));
        Assert.Equal(
            "TC3-HMAC-SHA256 Credential=AKIDEXAMPLE/2019-02-25/cvm/tc3_request, SignedHeaders=content-type;host;x-tc-action, "
            + "Signature=644be983de9a8a3f00db8eadaba61467c3b429e2215758ba897b738ca469fd26",
            signing.Authorization("AKIDEXAMPLE", "Gu5t9xGARNpq86cd98joQYCN3EXAMPLE"));
    }

    [Fact]
    public void 阿里云签名_与官方示例一致()
    {
        (string, string)[] parameters =
        [
            ("Version", "2014-05-26"), ("Action", "DescribeRegions"), ("AccessKeyId", "testid"), ("Format", "XML"),
            ("SignatureMethod", "HMAC-SHA1"), ("SignatureNonce", "3ee8c1b8-83d3-44af-a94f-4e0ad82fd6cf"),
            ("SignatureVersion", "1.0"), ("Timestamp", "2016-02-23T12:46:24Z"),
        ];

        Assert.StartsWith("GET&%2F&AccessKeyId%3Dtestid%26Action%3DDescribeRegions%26Format%3DXML", AlibabaProvider.StringToSign("GET", parameters));
        Assert.Equal("OLeaidS1JvxuMvnyHOwuJ+uX5qY=", AlibabaProvider.Signature("GET", parameters, "testsecret"));
    }

    [Fact]
    public void 阿里云_特殊字符按RFC3986编码()
    {
        Assert.Equal("POST&%2F&SourceText%3Da%2520b%252A~", AlibabaProvider.StringToSign("POST", [("SourceText", "a b*~")]));
    }

    [Fact]
    public void 火山引擎签名()
    {
        var signing = new VolcengineSigning("translate.volcengineapi.com", VolcengineProvider.Query,
            Encoding.UTF8.GetBytes("{\"TargetLanguage\":\"zh\",\"TextList\":[\"hello\"]}"), DateTimeOffset.FromUnixTimeSeconds(1_704_067_200));

        Assert.Equal("20240101T000000Z", signing.Timestamp);
        Assert.Equal("20240101/cn-north-1/translate/request", signing.CredentialScope);
        Assert.Equal("62d0880eb398b1203079c11e2608174c5b6200b6d195ae3e166484afb21f3daf", signing.Signature("testsecret"));
    }

    [Fact]
    public void DeepL地址_按Key后缀区分免费版()
    {
        Assert.Equal("https://api-free.deepl.com", DeepLProvider.Endpoint("abc:fx"));
        Assert.Equal("https://api.deepl.com", DeepLProvider.Endpoint("abc"));
    }

    [Fact]
    public void 各源的语言代码()
    {
        Assert.Equal("jp", BaiduProvider.Code(Language.Japanese));
        Assert.Equal("cht", BaiduProvider.Code(Language.TraditionalChinese));
        Assert.Equal("ara", BaiduProvider.Code(Language.Arabic));
        Assert.Equal("auto", BaiduProvider.Code(Language.Auto));
        Assert.Equal("zh-CHS", YoudaoProvider.Code(Language.SimplifiedChinese));
        Assert.Equal("zh-CHT", YoudaoProvider.Code(Language.TraditionalChinese));
        Assert.Equal("ja", YoudaoProvider.Code(Language.Japanese));
        Assert.Equal("zh-TW", TencentProvider.Code(Language.TraditionalChinese));
        Assert.Null(TencentProvider.Code(Language.Dutch));
        Assert.Equal("ZH-HANS", DeepLProvider.Code(Language.SimplifiedChinese));
        Assert.Equal("ZH", DeepLProvider.SourceCode(Language.SimplifiedChinese));
        Assert.Equal("EN-US", DeepLProvider.Code(Language.English));
        Assert.Equal("EN", DeepLProvider.SourceCode(Language.English));
        Assert.Null(DeepLProvider.Code(Language.Thai));
        Assert.Null(DeepLProvider.SourceCode(Language.Auto));
        Assert.Equal("zh-tw", AlibabaProvider.Code(Language.TraditionalChinese));
        Assert.Equal("zh-Hant", VolcengineProvider.Code(Language.TraditionalChinese));
        Assert.Equal("cht", NiutransProvider.Code(Language.TraditionalChinese));
        Assert.Equal("zh-Hant", CaiyunProvider.Code(Language.TraditionalChinese));
        Assert.Null(CaiyunProvider.Code(Language.Thai));
    }

    [Fact]
    public void 每个源都至少支持中英互译()
    {
        Func<Language, string?>[] maps =
        [
            BaiduProvider.Code, YoudaoProvider.Code, TencentProvider.Code, AlibabaProvider.Code, VolcengineProvider.Code,
            CaiyunProvider.Code, NiutransProvider.Code, DeepLProvider.Code, GoogleProvider.Code, BingProvider.Code, TransmartProvider.Code,
        ];
        Assert.All(maps, map =>
        {
            Assert.NotNull(map(Language.SimplifiedChinese));
            Assert.NotNull(map(Language.English));
        });
    }

    [Fact]
    public async Task 彩云_不支持的目标语言_直接报不支持_不发请求()
    {
        var provider = new CaiyunProvider("t", NoNetwork);

        var error = await Assert.ThrowsAsync<TranslationException>(() =>
            provider.TranslateAsync(new TranslationRequest("hi", Language.Auto, Language.French), _ => { }, default));

        Assert.Equal(TranslationException.Unsupported(Language.French).Message, error.Message);
    }

    [Fact]
    public async Task DeepL_没有Key也没有自定义地址_提示填写Key()
    {
        var provider = new DeepLProvider("", "", NoNetwork);

        var error = await Assert.ThrowsAsync<TranslationException>(() =>
            provider.TranslateAsync(new TranslationRequest("hi", Language.Auto, Language.English), _ => { }, default));

        Assert.Contains("Auth Key", error.Message);
    }

    [Fact]
    public void 工厂能创建每个需要Key的源_免费源不归它管()
    {
        foreach (var kind in new[] { ProviderKind.DeepL, ProviderKind.Baidu, ProviderKind.Youdao, ProviderKind.Tencent,
                     ProviderKind.Alibaba, ProviderKind.Volcengine, ProviderKind.Caiyun, ProviderKind.Niutrans })
        {
            Assert.NotNull(KeyedProviders.Make(kind, new Dictionary<string, string>(), NoNetwork));
        }

        Assert.Throws<TranslationException>(() => KeyedProviders.Make(ProviderKind.Google, new Dictionary<string, string>(), NoNetwork));
    }

    internal sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("测试里不应该发出网络请求");
    }
}
