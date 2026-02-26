namespace Mimir.Api.Services;

public class CaptchaService
{
    private readonly IHttpClientFactory _factory;
    private readonly string? _secret;

    public string Provider { get; }
    public string SiteKey { get; }

    public CaptchaService(IHttpClientFactory factory, IConfiguration cfg)
    {
        _factory = factory;

        // Provider resolution: Turnstile first, then reCAPTCHA, else none.
        if (!string.IsNullOrEmpty(cfg["TURNSTILE_SECRET"]))
        {
            Provider = "turnstile";
            SiteKey  = cfg["TURNSTILE_SITE_KEY"] ?? "";
            _secret  = cfg["TURNSTILE_SECRET"];
        }
        else if (!string.IsNullOrEmpty(cfg["RECAPTCHA_SECRET"]))
        {
            Provider = "recaptcha";
            SiteKey  = cfg["RECAPTCHA_SITE_KEY"] ?? "";
            _secret  = cfg["RECAPTCHA_SECRET"];
        }
        else
        {
            Provider = "";
            SiteKey  = "";
        }
    }

    /// <summary>
    /// Verifies the captcha token. Returns true if no provider is configured (dev bypass).
    /// </summary>
    public async Task<bool> VerifyAsync(string? token)
    {
        if (string.IsNullOrEmpty(Provider)) return true;
        if (string.IsNullOrEmpty(token))   return false;

        string url = Provider == "turnstile"
            ? "https://challenges.cloudflare.com/turnstile/v1/siteverify"
            : "https://www.google.com/recaptcha/api/siteverify";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"]   = _secret!,
            ["response"] = token
        });

        try
        {
            using var http = _factory.CreateClient();
            var res  = await http.PostAsync(url, form);
            var body = await res.Content.ReadFromJsonAsync<CaptchaVerifyResponse>();
            return body?.Success == true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CaptchaVerifyResponse(bool Success);
}
