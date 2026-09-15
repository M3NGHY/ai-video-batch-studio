namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    internal const string BridgeScript = """
        (() => {
          if (window.AIStudioBridge && window.AIStudioBridge.version === '2.0') return;

          const state = {
            taskId: null,
            seen: new Set(),
            timer: null,
            observer: null,
            fallbackTimer: null,
            best: null,
            playable: null
          };

          const post = payload => {
            try { window.chrome.webview.postMessage(JSON.stringify(payload)); } catch (_) {}
          };

          const normalize = raw => {
            if (!raw || typeof raw !== 'string') return '';
            return raw
              .replace(/\\u002F/gi, '/')
              .replace(/\\\//g, '/')
              .replace(/&amp;/g, '&')
              .trim();
          };

          const score = url => {
            let n = 0;
            if (/\.mp4(?:\?|$)/i.test(url)) n += 60;
            if (/h264|avc/i.test(url)) n += 100;
            if (/video_mp4|rc_gen_video|rc_video|video\/fplay/i.test(url)) n += 45;
            if (/h265|hevc/i.test(url)) n -= 30;
            if (/^https:\/\//i.test(url)) n += 10;
            return n;
          };

          const emitBest = () => {
            if (!state.best) return;
            const value = state.best;
            state.best = null;
            post({ type: 'videoCandidate', taskId: state.taskId, url: value.url, source: value.source, score: value.score });
          };

          const canUseForPlayback = url =>
            /^https:\/\//i.test(url) &&
            !/h265|hevc|hev1|hvc1/i.test(url) &&
            !/\/video\/fplay\//i.test(url) &&
            /(\.mp4(?:\?|$)|video_mp4|rc_gen_video|rc_video|h264|avc1)/i.test(url);

          const wireVideo = video => {
            if (!video || video.dataset.aiStudioPreviewWired === '1') return;
            video.dataset.aiStudioPreviewWired = '1';
            video.controls = true;
            video.playsInline = true;
            video.preload = 'metadata';
            const retry = () => setTimeout(() => repair(false), 80);
            video.addEventListener('error', retry, true);
            video.addEventListener('stalled', retry, true);
            video.addEventListener('emptied', retry, true);
          };

          const repair = force => {
            try {
              const replacement = state.playable?.url || '';
              let changed = 0;
              document.querySelectorAll('video').forEach(video => {
                wireVideo(video);
                if (!replacement || video.dataset.aiStudioPreviewUrl === replacement) return;

                const current = normalize(video.currentSrc || video.src || '');
                const noSource = !current || video.networkState === HTMLMediaElement.NETWORK_NO_SOURCE;
                const failed = !!video.error;
                const unsupported = /h265|hevc|hev1|hvc1/i.test(current);
                if (!force && !noSource && !failed && !unsupported) return;

                video.dataset.aiStudioPreviewUrl = replacement;
                video.src = replacement;
                video.controls = true;
                video.playsInline = true;
                video.preload = 'metadata';
                video.load();
                if (force) video.play().catch(() => {});
                changed++;
              });
              if (changed) post({ type: 'previewRepaired', taskId: state.taskId, count: changed, url: replacement });
              return changed;
            } catch (_) {
              return 0;
            }
          };

          const candidate = (raw, source) => {
            try {
              const url = normalize(raw);
              if (!/^https?:\/\//i.test(url)) return;
              if (!/(\.mp4(?:\?|$)|video\/fplay|video_mp4|rc_gen_video|rc_video|h264|avc|h265|hevc)/i.test(url)) return;
              if (state.seen.has(url)) return;
              state.seen.add(url);

              const item = { url, source, score: score(url) };
              if (!state.best || item.score > state.best.score) state.best = item;
              if (canUseForPlayback(url) && (!state.playable || item.score > state.playable.score)) {
                state.playable = item;
                setTimeout(() => repair(false), 0);
              }

              if (item.score >= 120) {
                if (state.fallbackTimer) clearTimeout(state.fallbackTimer);
                state.fallbackTimer = null;
                emitBest();
              } else if (!state.fallbackTimer) {
                state.fallbackTimer = setTimeout(() => {
                  state.fallbackTimer = null;
                  emitBest();
                }, 2500);
              }
            } catch (_) {}
          };

          const scanText = (text, source) => {
            try {
              if (!text || typeof text !== 'string') return;
              const normalized = normalize(text);
              const matches = normalized.match(/https?:\/\/[^\s"'<>]+/gi) || [];
              matches.slice(0, 80).forEach(url => candidate(url, source));
            } catch (_) {}
          };

          const scanDom = () => {
            try {
              document.querySelectorAll('video').forEach(v => {
                wireVideo(v);
                candidate(v.currentSrc, 'video.currentSrc');
                candidate(v.src, 'video.src');
                v.querySelectorAll('source').forEach(s => candidate(s.src, 'source.src'));
              });
              document.querySelectorAll('a[href]').forEach(a => candidate(a.href, 'anchor.href'));
              performance.getEntriesByType('resource').slice(-250).forEach(e => candidate(e.name, 'performance.resource'));
              repair(false);
            } catch (_) {}
          };

          if (!window.__AIStudioFetchHookedV2) {
            window.__AIStudioFetchHookedV2 = true;

            const originalFetch = window.fetch;
            window.fetch = async function(...args) {
              const response = await originalFetch.apply(this, args);
              try {
                candidate(response.url, 'fetch.response.url');
                const type = response.headers?.get?.('content-type') || '';
                if (/json|text|javascript/i.test(type)) {
                  response.clone().text().then(t => {
                    if (t && t.length <= 3000000) scanText(t, 'fetch.response.body');
                  }).catch(() => {});
                }
              } catch (_) {}
              return response;
            };

            const originalOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(...args) {
              this.addEventListener('load', () => {
                try {
                  candidate(this.responseURL, 'xhr.response.url');
                  if (!this.responseType || this.responseType === 'text') {
                    const t = this.responseText;
                    if (t && t.length <= 3000000) scanText(t, 'xhr.response.body');
                  }
                } catch (_) {}
              });
              return originalOpen.apply(this, args);
            };
          }

          window.AIStudioBridge = {
            version: '2.0',
            repair(force = true) {
              scanDom();
              return repair(!!force);
            },
            reset(taskId) {
              state.taskId = taskId;
              state.seen = new Set();
              state.best = null;
              state.playable = null;
              if (state.fallbackTimer) clearTimeout(state.fallbackTimer);
              state.fallbackTimer = null;

              document.querySelectorAll('video').forEach(v => {
                if (v.currentSrc) state.seen.add(normalize(v.currentSrc));
                if (v.src) state.seen.add(normalize(v.src));
              });
              performance.getEntriesByType('resource').forEach(e => {
                const u = normalize(e.name);
                if (u) state.seen.add(u);
              });

              if (state.observer) state.observer.disconnect();
              state.observer = new MutationObserver(scanDom);
              state.observer.observe(document.documentElement, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['src', 'href']
              });

              if (state.timer) clearInterval(state.timer);
              state.timer = setInterval(scanDom, 1500);
              post({ type: 'log', message: '等待新视频结果（DOM + 网络 + 资源监听）...', taskId });
            }
          };
        })();
        """;
}
