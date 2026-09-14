using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIVideoBatchStudio;

internal static class DolaSessionAccountExtensions
{
    private const string DolaUrl = "https://www.dola.com/chat";

    public static async Task<string> LoginGoogleAccountAsync(
        this DolaSession session,
        GoogleAccount account,
        CancellationToken cancellationToken)
    {
        if (session.Browser.CoreWebView2 is null)
            throw new InvalidOperationException($"{session.Name} 尚未初始化。");

        var core = session.Browser.CoreWebView2;
        EventHandler<CoreWebView2NewWindowRequestedEventArgs>? popupHandler = null;
        popupHandler = (_, e) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.Uri)) return;
                e.Handled = true;
                core.Navigate(e.Uri);
            }
            catch
            {
                // Ignore popup handoff errors. The caller will time out and report the page state.
            }
        };
        core.NewWindowRequested += popupHandler;

        try
        {
            // Critical: always start from Dola. We do not navigate to a generic Google sign-in page,
            // because that can end at the Google account/profile page instead of returning to Dola OAuth.
            await NavigateAndWaitAsync(session, DolaUrl, cancellationToken);

            var launched = await LaunchGoogleOAuthFromDolaAsync(session, cancellationToken);
            if (!launched)
                return "未在 Dola 页面找到 Google 登录入口，请确认当前窗口处于 Dola 登录页面。";

            var emailJson = JsonSerializer.Serialize(account.Email);
            var passwordJson = JsonSerializer.Serialize(account.Password);
            var sawGoogle = false;
            var emailSubmitted = false;
            var passwordSubmitted = false;
            var deadline = DateTime.UtcNow.AddMinutes(2);

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = core.Source ?? string.Empty;

                if (IsDolaUrl(source) && sawGoogle)
                {
                    await Task.Delay(1200, cancellationToken);
                    return "DOLA_LOGIN_OK";
                }

                if (IsGoogleUrl(source))
                {
                    sawGoogle = true;

                    if (await IsGoogleChallengeAsync(core))
                        return "Google 要求验证码/二次验证，请在该 Dola 窗口中手动完成；完成后页面会回到 Dola。";

                    // Account chooser: use the exact imported account when already present in this Profile.
                    if (await TryChooseExistingGoogleAccountAsync(core, emailJson))
                    {
                        await Task.Delay(900, cancellationToken);
                        continue;
                    }

                    // If another Google account is cached, choose "Use another account" before filling email.
                    if (!emailSubmitted && await TryClickUseAnotherAccountAsync(core))
                    {
                        await Task.Delay(700, cancellationToken);
                        continue;
                    }

                    if (!emailSubmitted && await HasSelectorAsync(core, "input[type='email']"))
                    {
                        var ok = await FillGoogleEmailAsync(core, emailJson);
                        if (!ok) return "Google 邮箱填写失败。";
                        emailSubmitted = true;
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    if (!passwordSubmitted && await HasSelectorAsync(core, "input[type='password']"))
                    {
                        var ok = await FillGooglePasswordAsync(core, passwordJson);
                        if (!ok) return "Google 密码填写失败。";
                        passwordSubmitted = true;
                        await Task.Delay(1200, cancellationToken);
                        continue;
                    }

                    // Standard Google OAuth consent/continue screens. This only continues the Dola login
                    // the user explicitly requested; it does not attempt to bypass verification challenges.
                    if (await TryClickGoogleConsentAsync(core))
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }
                }
                else if (!sawGoogle && IsDolaUrl(source))
                {
                    // Some Dola builds open a login modal asynchronously; try the Google entry again.
                    await TryClickGoogleEntryAsync(core);
                }

                await Task.Delay(500, cancellationToken);
            }

            var finalUrl = core.Source ?? string.Empty;
            if (IsDolaUrl(finalUrl))
                return "DOLA_LOGIN_OK";
            if (IsGoogleUrl(finalUrl))
                return "Google 登录尚未完成，请检查该窗口是否需要验证码、账号确认或授权确认。";
            return $"Google 登录超时，当前页面：{finalUrl}";
        }
        finally
        {
            core.NewWindowRequested -= popupHandler;
        }
    }

    public static async Task<int> ImportDolaCookiesAsync(
        this DolaSession session,
        IReadOnlyList<ImportedCookie> cookies,
        CancellationToken cancellationToken)
    {
        if (session.Browser.CoreWebView2 is null)
            throw new InvalidOperationException($"{session.Name} 尚未初始化。");
        if (cookies.Count == 0) return 0;

        var manager = session.Browser.CoreWebView2.CookieManager;

        // Clear only Dola cookies from this isolated Profile before importing the selected account.
        foreach (var url in new[] { "https://www.dola.com/", "https://dola.com/" })
        {
            var existing = await manager.GetCookiesAsync(url);
            foreach (var cookie in existing) manager.DeleteCookie(cookie);
        }

        var imported = 0;
        foreach (var source in cookies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source.Name)) continue;

            var domain = NormalizeDolaCookieDomain(source.Domain);
            if (domain is null) continue;

            var path = string.IsNullOrWhiteSpace(source.Path) ? "/" : source.Path.Trim();
            var cookie = manager.CreateCookie(source.Name.Trim(), source.Value ?? string.Empty, domain, path);
            cookie.IsSecure = source.Secure || domain.Contains("dola.com", StringComparison.OrdinalIgnoreCase);
            cookie.IsHttpOnly = source.HttpOnly;
            if (source.Expires is { } expires && expires > DateTime.UtcNow)
                cookie.Expires = expires;
            manager.AddOrUpdateCookie(cookie);
            imported++;
        }

        if (imported == 0)
            throw new InvalidOperationException("Cookie 文件中没有可用于 dola.com 的 Cookie。 ");

        await NavigateAndWaitAsync(session, DolaUrl, cancellationToken);
        await Task.Delay(800, cancellationToken);
        return imported;
    }

    private static async Task<bool> LaunchGoogleOAuthFromDolaAsync(
        DolaSession session,
        CancellationToken cancellationToken)
    {
        var core = session.Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器未初始化。");

        // First try a Google entry that is already visible (login modal/page).
        if (await TryClickGoogleEntryAsync(core))
        {
            await Task.Delay(1000, cancellationToken);
            return true;
        }

        // Otherwise click Dola's login/sign-in entry, wait for its dialog, then click Google.
        var loginClicked = await core.ExecuteScriptAsync("""
            (() => {
              const visible = el => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              };
              const textOf = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const nodes = [...document.querySelectorAll('button,a,[role="button"]')].filter(visible);
              const target = nodes.find(el => /^(登录|登入|log\s*in|login|sign\s*in)$/i.test(textOf(el))) ||
                             nodes.find(el => /(登录|登入|log\s*in|sign\s*in)/i.test(textOf(el)));
              if (!target) return false;
              target.click();
              return true;
            })();
            """);

        if (!loginClicked.Contains("true", StringComparison.OrdinalIgnoreCase))
            return false;

        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsGoogleUrl(core.Source ?? string.Empty)) return true;
            if (await TryClickGoogleEntryAsync(core))
            {
                await Task.Delay(900, cancellationToken);
                return true;
            }
            await Task.Delay(350, cancellationToken);
        }

        return IsGoogleUrl(core.Source ?? string.Empty);
    }

    private static async Task<bool> TryClickGoogleEntryAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const visible = el => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              };
              const textOf = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const nodes = [...document.querySelectorAll('button,a,[role="button"]')].filter(visible);
              let target = nodes.find(el => /google/i.test(textOf(el)));
              if (!target) {
                const img = [...document.querySelectorAll('img[alt],svg')].find(el => /google/i.test(el.getAttribute('alt') || el.getAttribute('aria-label') || ''));
                target = img?.closest('button,a,[role="button"]') || null;
              }
              if (!target) return false;
              target.click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryChooseExistingGoogleAccountAsync(CoreWebView2 core, string emailJson)
    {
        var result = await core.ExecuteScriptAsync($$"""
            (() => {
              const email = {{emailJson}};
              const exact = document.querySelector(`[data-identifier="${CSS.escape(email)}"]`);
              if (exact) { (exact.closest('[role="link"],[role="button"],button') || exact).click(); return true; }
              const nodes = [...document.querySelectorAll('[data-email],[role="link"],[role="button"],li,div')];
              const match = nodes.find(x => {
                const v = x.getAttribute('data-email') || x.getAttribute('data-identifier') || (x.innerText || '').trim();
                return v === email || v.includes(email);
              });
              if (!match) return false;
              (match.closest('[role="link"],[role="button"],button') || match).click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryClickUseAnotherAccountAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const nodes = [...document.querySelectorAll('button,[role="button"],[role="link"],div')];
              const target = nodes.find(x => /use another account|使用其他账号|使用其他帐号|换一个账号|another account/i.test((x.innerText || x.textContent || '').trim()));
              if (!target) return false;
              (target.closest('button,[role="button"],[role="link"]') || target).click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> FillGoogleEmailAsync(CoreWebView2 core, string emailJson)
    {
        var result = await core.ExecuteScriptAsync($$"""
            (() => {
              const input = document.querySelector("input[type='email']");
              if (!input) return false;
              const value = {{emailJson}};
              input.focus();
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setter) setter.call(input, value); else input.value = value;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              const next = document.querySelector('#identifierNext') ||
                [...document.querySelectorAll('button,[role="button"]')].find(b => /下一步|next/i.test((b.innerText || b.textContent || '').trim()));
              if (next) next.click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> FillGooglePasswordAsync(CoreWebView2 core, string passwordJson)
    {
        var result = await core.ExecuteScriptAsync($$"""
            (() => {
              const input = document.querySelector("input[type='password']");
              if (!input) return false;
              const value = {{passwordJson}};
              input.focus();
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setter) setter.call(input, value); else input.value = value;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              const next = document.querySelector('#passwordNext') ||
                [...document.querySelectorAll('button,[role="button"]')].find(b => /下一步|next/i.test((b.innerText || b.textContent || '').trim()));
              if (next) next.click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryClickGoogleConsentAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const visible = el => {
                const r = el.getBoundingClientRect();
                return r.width > 0 && r.height > 0;
              };
              const nodes = [...document.querySelectorAll('button,[role="button"]')].filter(visible);
              const target = nodes.find(x => /^(continue|继续|allow|允许|confirm|确认)$/i.test((x.innerText || x.textContent || '').trim()));
              if (!target) return false;
              target.click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsGoogleChallengeAsync(CoreWebView2 core)
    {
        var source = core.Source ?? string.Empty;
        if (source.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("speedbump", StringComparison.OrdinalIgnoreCase))
            return true;

        var result = await core.ExecuteScriptAsync("""
            (() => {
              const text = (document.body?.innerText || '').slice(0, 10000);
              return /2-Step Verification|两步验证|两步驗證|验证您的身份|Verify it.?s you|Confirm it.?s you|验证码|security key/i.test(text);
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasSelectorAsync(CoreWebView2 core, string selector)
    {
        var selectorJson = JsonSerializer.Serialize(selector);
        var result = await core.ExecuteScriptAsync($"Boolean(document.querySelector({selectorJson}))");
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeDolaCookieDomain(string? raw)
    {
        var domain = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(domain)) return ".dola.com";
        if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(domain, UriKind.Absolute, out var uri)) return null;
            domain = uri.Host;
        }
        domain = domain.TrimStart('.');
        if (!domain.Equals("dola.com", StringComparison.OrdinalIgnoreCase) &&
            !domain.EndsWith(".dola.com", StringComparison.OrdinalIgnoreCase))
            return null;
        return "." + domain;
    }

    private static bool IsGoogleUrl(string value) =>
        value.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("google.com/o/oauth", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDolaUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("dola.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".dola.com", StringComparison.OrdinalIgnoreCase));

    private static async Task NavigateAndWaitAsync(DolaSession session, string url, CancellationToken cancellationToken)
    {
        var core = session.Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器未初始化。");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            await tcs.Task;
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }
}
