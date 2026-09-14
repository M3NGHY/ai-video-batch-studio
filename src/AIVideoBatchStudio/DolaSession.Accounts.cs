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
        await NavigateAndWaitAsync(session, "https://accounts.google.com/signin/v2/identifier?hl=zh-CN", cancellationToken);

        if (!await WaitForSelectorAsync(session, "input[type='email']", TimeSpan.FromSeconds(30), cancellationToken))
            return "未找到 Google 邮箱输入框，可能需要手动确认登录页面。";

        var emailJson = JsonSerializer.Serialize(account.Email);
        var emailResult = await core.ExecuteScriptAsync($$"""
            (() => {
              const input = document.querySelector("input[type='email']");
              if (!input) return false;
              const value = {{emailJson}};
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setter) setter.call(input, value); else input.value = value;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              const next = document.querySelector('#identifierNext') ||
                [...document.querySelectorAll('button')].find(b => /下一步|next/i.test((b.innerText || '').trim()));
              if (next) next.click();
              return true;
            })();
            """);
        if (!emailResult.Contains("true", StringComparison.OrdinalIgnoreCase))
            return "Google 邮箱填写失败。";

        if (!await WaitForSelectorAsync(session, "input[type='password']", TimeSpan.FromSeconds(35), cancellationToken))
        {
            var url = core.Source ?? string.Empty;
            if (url.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                return "Google 要求验证码/二次验证，请在该 Dola 窗口中手动完成；软件不会绕过验证。";
            return "未出现 Google 密码输入框，可能需要验证码或额外确认。";
        }

        var passwordJson = JsonSerializer.Serialize(account.Password);
        var passwordResult = await core.ExecuteScriptAsync($$"""
            (() => {
              const input = document.querySelector("input[type='password']");
              if (!input) return false;
              const value = {{passwordJson}};
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setter) setter.call(input, value); else input.value = value;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              const next = document.querySelector('#passwordNext') ||
                [...document.querySelectorAll('button')].find(b => /下一步|next/i.test((b.innerText || '').trim()));
              if (next) next.click();
              return true;
            })();
            """);
        if (!passwordResult.Contains("true", StringComparison.OrdinalIgnoreCase))
            return "Google 密码填写失败。";

        await Task.Delay(2500, cancellationToken);
        var state = await WaitForGoogleLoginResultAsync(session, cancellationToken);
        if (state == GoogleLoginState.NeedsVerification)
            return "Google 要求验证码/二次验证，请在该窗口手动完成；完成后可继续使用当前 Profile。";
        if (state == GoogleLoginState.Timeout)
            return "Google 登录状态未能自动确认，请检查该窗口页面。";

        await NavigateAndWaitAsync(session, DolaUrl, cancellationToken);

        EventHandler<CoreWebView2NewWindowRequestedEventArgs>? popupHandler = null;
        popupHandler = (_, e) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.Uri)) return;
                if (!e.Uri.Contains("google", StringComparison.OrdinalIgnoreCase) &&
                    !e.Uri.Contains("dola.com", StringComparison.OrdinalIgnoreCase)) return;
                e.Handled = true;
                core.Navigate(e.Uri);
            }
            catch { }
        };
        core.NewWindowRequested += popupHandler;

        try
        {
            var clickGoogle = await core.ExecuteScriptAsync("""
                (() => {
                  const all = [...document.querySelectorAll('button,a,[role="button"]')];
                  const target = all.find(x => /google/i.test((x.innerText || x.textContent || x.getAttribute('aria-label') || '').trim()));
                  if (!target) return false;
                  target.click();
                  return true;
                })();
                """);

            if (!clickGoogle.Contains("true", StringComparison.OrdinalIgnoreCase))
                return "Google 账号已登录到独立 Profile；Dola 页面没有检测到 Google 登录按钮。";

            await Task.Delay(1800, cancellationToken);
            if ((core.Source ?? string.Empty).Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            {
                var accountJson = JsonSerializer.Serialize(account.Email);
                await core.ExecuteScriptAsync($$"""
                    (() => {
                      const email = {{accountJson}};
                      const exact = document.querySelector(`[data-identifier="${email}"]`);
                      if (exact) { exact.click(); return true; }
                      const nodes = [...document.querySelectorAll('[role="link"],[role="button"],div')];
                      const match = nodes.find(x => (x.innerText || '').trim() === email);
                      if (match) { match.click(); return true; }
                      return false;
                    })();
                    """);
            }

            await Task.Delay(2500, cancellationToken);
            return "Google 登录已提交，并已触发 Dola 的 Google 登录流程。";
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

            var domain = string.IsNullOrWhiteSpace(source.Domain) ? ".dola.com" : source.Domain.Trim();
            var path = string.IsNullOrWhiteSpace(source.Path) ? "/" : source.Path;
            var cookie = manager.CreateCookie(source.Name, source.Value ?? string.Empty, domain, path);
            cookie.IsSecure = source.Secure || domain.Contains("dola.com", StringComparison.OrdinalIgnoreCase);
            cookie.IsHttpOnly = source.HttpOnly;
            if (source.Expires is { } expires && expires > DateTime.UtcNow)
                cookie.Expires = expires;
            manager.AddOrUpdateCookie(cookie);
            imported++;
        }

        await NavigateAndWaitAsync(session, DolaUrl, cancellationToken);
        return imported;
    }

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
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            await tcs.Task;
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    private static async Task<bool> WaitForSelectorAsync(
        DolaSession session,
        string selector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var core = session.Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器未初始化。");
        var selectorJson = JsonSerializer.Serialize(selector);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await core.ExecuteScriptAsync($"Boolean(document.querySelector({selectorJson}))");
            if (result.Contains("true", StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    private static async Task<GoogleLoginState> WaitForGoogleLoginResultAsync(
        DolaSession session,
        CancellationToken cancellationToken)
    {
        var core = session.Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器未初始化。");
        var until = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = core.Source ?? string.Empty;
            if (url.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("signin/v2/challenge", StringComparison.OrdinalIgnoreCase))
                return GoogleLoginState.NeedsVerification;

            var hasPassword = await core.ExecuteScriptAsync("Boolean(document.querySelector(\"input[type='password']\"))");
            if (!hasPassword.Contains("true", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("signin/v2/identifier", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("signin/v2/challenge", StringComparison.OrdinalIgnoreCase))
                return GoogleLoginState.SignedIn;

            await Task.Delay(750, cancellationToken);
        }
        return GoogleLoginState.Timeout;
    }

    private enum GoogleLoginState
    {
        SignedIn,
        NeedsVerification,
        Timeout
    }
}
