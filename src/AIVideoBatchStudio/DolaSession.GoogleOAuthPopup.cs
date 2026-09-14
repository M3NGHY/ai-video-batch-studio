using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal static class DolaSessionGoogleOAuthPopup
{
    private const string DolaUrl = "https://www.dola.com/chat";

    public static async Task<string> OpenGoogleOAuthForAccountAsync(
        this DolaSession session,
        string email,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        var main = session.Browser.CoreWebView2
            ?? throw new InvalidOperationException($"{session.Name} 尚未初始化。");

        await NavigateAndWaitAsync(main, DolaUrl, cancellationToken);

        using var authForm = new Form
        {
            Text = $"{session.Name} - Google 登录",
            Width = 560,
            Height = 760,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true
        };
        using var authView = new WebView2 { Dock = DockStyle.Fill };
        authForm.Controls.Add(authView);
        authForm.Show();
        await authView.EnsureCoreWebView2Async(main.Environment);
        authView.CoreWebView2.Settings.IsStatusBarEnabled = false;

        var authCore = authView.CoreWebView2;
        var oauthOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        authForm.FormClosed += (_, _) => closed = true;

        void NewWindowHandler(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Uri) || !IsGoogleOrDola(e.Uri)) return;
            e.NewWindow = authCore;
            oauthOpened.TrySetResult(true);
            authForm.Activate();
        }

        void MainNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!IsGoogleUrl(e.Uri)) return;
            e.Cancel = true;
            authCore.Navigate(e.Uri);
            oauthOpened.TrySetResult(true);
            authForm.Activate();
        }

        main.NewWindowRequested += NewWindowHandler;
        main.NavigationStarting += MainNavigationStarting;

        try
        {
            progress?.Invoke("正在从 Dola 发起 Google OAuth...");
            if (!await LaunchGoogleFromDolaAsync(main, cancellationToken))
                return "未在 Dola 页面找到 Google 登录入口。";

            using (var waitOpen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                waitOpen.CancelAfter(TimeSpan.FromSeconds(20));
                using var reg = waitOpen.Token.Register(() => oauthOpened.TrySetCanceled(waitOpen.Token));
                try { await oauthOpened.Task; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return "Dola 已触发登录，但没有打开 Google OAuth 页面。";
                }
            }

            var emailJson = JsonSerializer.Serialize(email);
            var emailFilled = false;
            var deadline = DateTime.UtcNow.AddMinutes(4);

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (closed) return "Google 登录窗口已关闭。";

                var source = authCore.Source ?? string.Empty;
                if (IsDolaUrl(source))
                {
                    progress?.Invoke("Google OAuth 已回调 Dola，正在刷新主页面...");
                    await Task.Delay(800, cancellationToken);
                    await NavigateAndWaitAsync(main, DolaUrl, cancellationToken);
                    return "DOLA_LOGIN_OK";
                }

                if (IsGoogleUrl(source) && !emailFilled)
                {
                    var result = await authCore.ExecuteScriptAsync($$"""
                        (() => {
                          const input = document.querySelector("input[type='email'],#identifierId,input[name='identifier']");
                          if (!input) return false;
                          const value = {{emailJson}};
                          input.focus();
                          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                          if (setter) setter.call(input, value); else input.value = value;
                          input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
                          input.dispatchEvent(new Event('change', { bubbles: true }));
                          const next = document.querySelector('#identifierNext button,#identifierNext') ||
                            [...document.querySelectorAll('button,[role="button"]')].find(x => /下一步|next/i.test((x.innerText || x.textContent || '').trim()));
                          if (next) next.click();
                          return true;
                        })();
                        """);
                    if (result.Contains("true", StringComparison.OrdinalIgnoreCase))
                    {
                        emailFilled = true;
                        progress?.Invoke("已填写 Google 邮箱，请在弹出的 Google 窗口完成密码/验证。 ");
                    }
                }

                await Task.Delay(500, cancellationToken);
            }

            return "等待 Google OAuth 回调超时。";
        }
        finally
        {
            main.NewWindowRequested -= NewWindowHandler;
            main.NavigationStarting -= MainNavigationStarting;
            try { if (!authForm.IsDisposed) authForm.Close(); } catch { }
        }
    }

    private static async Task<bool> LaunchGoogleFromDolaAsync(CoreWebView2 core, CancellationToken cancellationToken)
    {
        if (await TryClickGoogleAsync(core)) return true;

        var login = await core.ExecuteScriptAsync("""
            (() => {
              const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
              const text = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const nodes = [...document.querySelectorAll('button,a,[role="button"]')].filter(visible);
              const target = nodes.find(x => /^(登录|登入|login|log in|sign in)$/i.test(text(x))) ||
                             nodes.find(x => /登录|登入|login|log in|sign in/i.test(text(x)));
              if (!target) return false;
              target.click();
              return true;
            })();
            """);
        if (!login.Contains("true", StringComparison.OrdinalIgnoreCase)) return false;

        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryClickGoogleAsync(core)) return true;
            await Task.Delay(300, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> TryClickGoogleAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
              const text = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const nodes = [...document.querySelectorAll('button,a,[role="button"]')].filter(visible);
              let target = nodes.find(x => /google/i.test(text(x)));
              if (!target) {
                const img = [...document.querySelectorAll('img')].find(x => /google/i.test((x.alt || x.src || '').toLowerCase()));
                target = img?.closest('button,a,[role="button"]') || null;
              }
              if (!target) return false;
              target.click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            using var reg = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            await tcs.Task;
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    private static bool IsGoogleUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("google.com/o/oauth", StringComparison.OrdinalIgnoreCase));

    private static bool IsDolaUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("dola.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".dola.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsGoogleOrDola(string? value) => IsGoogleUrl(value) || IsDolaUrl(value);
}
