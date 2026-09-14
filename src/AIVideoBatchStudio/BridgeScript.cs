namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    internal const string BridgeScript = """
        (() => {
          if (window.AIStudioBridge && window.AIStudioBridge.version === '1.0') return;
          const state = { taskId: null, seen: new Set(), timer: null, observer: null };
          const post = (payload) => {
            try { window.chrome.webview.postMessage(JSON.stringify(payload)); } catch (_) {}
          };
          const candidate = (raw, source) => {
            try {
              if (!raw || typeof raw !== 'string') return;
              let url = raw.trim();
              if (!/^https?:\/\//i.test(url)) return;
              if (!/(\.mp4(?:\?|$)|video\/fplay|video_mp4|rc_gen_video|rc_video|h264)/i.test(url)) return;
              if (state.seen.has(url)) return;
              state.seen.add(url);
              post({ type: 'videoCandidate', taskId: state.taskId, url, source });
            } catch (_) {}
          };
          const scan = () => {
            document.querySelectorAll('video').forEach(v => {
              candidate(v.currentSrc, 'video.currentSrc');
              candidate(v.src, 'video.src');
              v.querySelectorAll('source').forEach(s => candidate(s.src, 'source.src'));
            });
          };
          if (!window.__AIStudioFetchHooked) {
            window.__AIStudioFetchHooked = true;
            const originalFetch = window.fetch;
            window.fetch = async function(...args) {
              const response = await originalFetch.apply(this, args);
              try { candidate(response.url, 'fetch.response'); } catch (_) {}
              return response;
            };
            const originalOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(...args) {
              this.addEventListener('load', () => {
                try { candidate(this.responseURL, 'xhr.response'); } catch (_) {}
              });
              return originalOpen.apply(this, args);
            };
          }
          window.AIStudioBridge = {
            version: '1.0',
            reset(taskId) {
              state.taskId = taskId;
              state.seen = new Set();
              document.querySelectorAll('video').forEach(v => {
                if (v.currentSrc) state.seen.add(v.currentSrc);
                if (v.src) state.seen.add(v.src);
              });
              if (state.observer) state.observer.disconnect();
              state.observer = new MutationObserver(scan);
              state.observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
              if (state.timer) clearInterval(state.timer);
              state.timer = setInterval(scan, 1500);
              post({ type: 'log', message: '等待新视频结果...', taskId });
            }
          };
        })();
        """;
}
