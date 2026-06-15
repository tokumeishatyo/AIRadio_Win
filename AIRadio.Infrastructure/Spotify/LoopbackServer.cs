using System.Net;
using System.Text;
using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// OAuth リダイレクト用のローカル HTTP サーバ。`127.0.0.1:&lt;port&gt;/callback?code=...` を 1 回受領し、
/// 認可コードを返す（<see cref="HttpListener"/>）。
/// </summary>
public sealed class LoopbackServer
{
    public async Task<string> WaitForCodeAsync(ushort port, CancellationToken ct = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            using var registration = ct.Register(() =>
            {
                try { listener.Stop(); } catch { /* 停止時の例外は無視 */ }
            });

            var context = await listener.GetContextAsync().ConfigureAwait(false);

            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            const string html = "<html><head><meta charset=\"utf-8\"></head><body>認証が完了しました。このウィンドウを閉じてください。</body></html>";
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, ct).ConfigureAwait(false);
            context.Response.Close();

            if (string.IsNullOrEmpty(code))
            {
                throw SpotifyException.AuthFailed(error ?? "認可コードを取得できませんでした");
            }
            return code;
        }
        finally
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
    }
}
