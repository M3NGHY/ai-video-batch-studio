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
        string password,
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
            var passwordJson = JsonSerializer.Serialize(password);
            var emailFilled = false;
            var passwordFilled = false;
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

                if (IsGoogleUrl(source))
                {
                    var result = await authCore.ExecuteScriptAsync($$"""
                        (() => {
                          const email = {{emailJson}};
                          const password = {{passwordJson}};
                          const visible = el => {
                            if (!el) return false;
                            const r = el.getBoundingClientRect();
                            const s = getComputedStyle(el);
                            return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
                          };
                          const text = el => (el.innerText || el.textContent || el.getAttribute?.('aria-label') || el.getAttribute?.('title') || '').replace(/\s+/g, ' ').trim();
                          const click = el => {
                            const target = el?.closest?.('button,[role="button"],[role="link"],a') || el;
                            if (!target) return false;
                            target.click();
                            return true;
                          };

                          const passwordInput = [...document.querySelectorAll("input[type='password']")].find(visible);
                          if (passwordInput) {
                            if (passwordInput.dataset.aiStudioSubmitted === '1') return 'WAIT';
                            passwordInput.dataset.aiStudioSubmitted = '1';
                            passwordInput.focus();
                            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                            if (setter) setter.call(passwordInput, password); else passwordInput.value = password;
                            passwordInput.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: password }));
                            passwordInput.dispatchEvent(new Event('change', { bubbles: true }));
                            const next = document.querySelector('#passwordNext button,#passwordNext') ||
                              [...document.querySelectorAll('button,[role="button"]')].filter(visible).find(x => /^(下一步|next)$/i.test(text(x)));
                            if (next) next.click();
                            return 'PASSWORD';
                          }

                          const emailInput = [...document.querySelectorAll("input[type='email'],#identifierId,input[name='identifier']")].find(visible);
                          if (emailInput) {
                            if (emailInput.dataset.aiStudioSubmitted === '1') return 'WAIT';
                            emailInput.dataset.aiStudioSubmitted = '1';
                            emailInput.focus();
                            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                            if (setter) setter.call(emailInput, email); else emailInput.value = email;
                            emailInput.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: email }));
                            emailInput.dispatchEvent(new Event('change', { bubbles: true }));
                            const next = document.querySelector('#identifierNext button,#identifierNext') ||
                              [...document.querySelectorAll('button,[role="button"]')].filter(visible).find(x => /^(下一步|next)$/i.test(text(x)));
                            if (next) next.click();
                            return 'EMAIL';
                          }

                          const exact = document.querySelector(`[data-identifier="${CSS.escape(email)}"],[data-email="${CSS.escape(email)}"]`);
                          if (exact && click(exact)) return 'ACCOUNT';

                          const accountNodes = [...document.querySelectorAll('[data-identifier],[data-email],[role="link"],[role="button"]')];
                          const account = accountNodes.find(x => text(x).includes(email));
                          if (account && click(account)) return 'ACCOUNT';

                          const another = [...document.querySelectorAll('button,[role="button"],[role="link"],div')]
                            .find(x => /use another account|使用其他账号|使用其他帐号|换一个账号/i.test(text(x)));
                          if (another && click(another)) return 'ANOTHER';

                          const consent = [...document.querySelectorAll('button,[role="button"]')].filter(visible)
                            .find(x => /^(continue|继续|allow|允许|confirm|确认)$/i.test(text(x)));
                          if (consent && click(consent)) return 'CONSENT';
                          return 'WAIT';
                        })();
                        """);
                    if (result.Contains("EMAIL", StringComparison.OrdinalIgnoreCase) && !emailFilled)
                    {
                        emailFilled = true;
                        progress?.Invoke("已自动填写 Google 邮箱。 ");
                    }
                    if (result.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) && !passwordFilled)
                    {
                        passwordFilled = true;
                        progress?.Invoke("已自动填写 Google 密码；如出现验证码或二次验证，请在弹窗中完成。 ");
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
        // Dola is client-rendered. NavigationCompleted can fire before its login UI exists.
        // Keep probing the page rather than treating the first missing element as a failure.
        var pageDeadline = DateTime.UtcNow.AddSeconds(30);
        var loginClicked = false;
        while (DateTime.UtcNow < pageDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryClickGoogleAsync(core)) return true;

            if (!loginClicked)
                loginClicked = await TryClickDolaLoginAsync(core);

            await Task.Delay(loginClicked ? 350 : 500, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> TryClickDolaLoginAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const roots = [document];
              for (let i = 0; i < roots.length; i++) {
                const root = roots[i];
                try {
                  root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) roots.push(el.shadowRoot); });
                  root.querySelectorAll('iframe').forEach(frame => { if (frame.contentDocument) roots.push(frame.contentDocument); });
                } catch (_) {}
              }
              const visible = el => {
                if (!el) return false;
                const r = el.getBoundingClientRect();
                const s = (el.ownerDocument?.defaultView || window).getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.pointerEvents !== 'none';
              };
              const text = el => (el.innerText || el.textContent || el.value || el.getAttribute?.('aria-label') || el.getAttribute?.('title') || el.getAttribute?.('data-testid') || '').replace(/\s+/g, ' ').trim();
              const nodes = roots.flatMap(root => {
                try { return [...root.querySelectorAll('button,a,[role="button"],[role="link"],input[type="button"],input[type="submit"],[tabindex]')]; }
                catch (_) { return []; }
              }).filter(visible);
              const target = nodes.find(x => /^(登录|登入|登陆|login|log in|sign in)$/i.test(text(x))) ||
                             nodes.find(x => /登录|登入|登陆|login|log in|sign in/i.test(text(x)));
              if (!target) return false;
              (target.closest?.('button,a,[role="button"],[role="link"]') || target).click();
              return true;
            })();
            """);
        return result.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryClickGoogleAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const roots = [document];
              for (let i = 0; i < roots.length; i++) {
                const root = roots[i];
                try {
                  root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) roots.push(el.shadowRoot); });
                  root.querySelectorAll('iframe').forEach(frame => { if (frame.contentDocument) roots.push(frame.contentDocument); });
                } catch (_) {}
              }
              const visible = el => {
                if (!el) return false;
                const r = el.getBoundingClientRect();
                const s = (el.ownerDocument?.defaultView || window).getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              };
              const text = el => (el.innerText || el.textContent || el.value || el.getAttribute?.('aria-label') || el.getAttribute?.('title') || '').replace(/\s+/g, ' ').trim();
              const nodes = roots.flatMap(root => {
                try { return [...root.querySelectorAll('button,a,[role="button"],[role="link"]')]; }
                catch (_) { return []; }
              }).filter(visible);
              let target = nodes.find(x => /google/i.test(text(x)));
              if (!target) {
                const images = roots.flatMap(root => {
                  try { return [...root.querySelectorAll('img,svg')]; } catch (_) { return []; }
                });
                const img = images.find(x => /google/i.test((x.alt || x.src?.baseVal || x.src || x.getAttribute?.('aria-label') || '').toLowerCase()));
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
