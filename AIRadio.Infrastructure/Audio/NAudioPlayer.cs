using AIRadio.Core;
using NAudio.Wave;

namespace AIRadio.Infrastructure;

/// <summary>
/// NAudio による WAV 再生。再生完了まで待機し、キャンセル時は停止する（完全静寂。CLAUDE.md §3-1）。
/// Mac の AVAudioPlayer（duration+0.2s スリープ近似）に対し、WaveOutEvent の PlaybackStopped
/// イベントで実際の再生終了を待つ。
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private readonly float _volume;

    public NAudioPlayer(float volume = 1.0f) => _volume = Math.Clamp(volume, 0.0f, 1.0f);

    /// <summary>再生音量（0.0–1.0）。config/tts.yaml の playback_volume。</summary>
    public float Volume => _volume;

    public async Task PlayAsync(byte[] wav, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        WaveFileReader reader;
        try
        {
            reader = new WaveFileReader(new MemoryStream(wav));
        }
        catch (Exception ex)
        {
            throw AudioException.PlaybackFailed(ex); // 不正な WAV データ
        }

        using (reader)
        using (var output = new WaveOutEvent())
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    finished.TrySetException(e.Exception);
                }
                else
                {
                    finished.TrySetResult();
                }
            };

            try
            {
                output.Init(reader);
                output.Volume = _volume;
                output.Play();
            }
            catch (Exception ex)
            {
                throw AudioException.PlaybackFailed(ex); // デバイス初期化・再生開始失敗
            }

            // キャンセル時は即停止（PlaybackStopped が発火して待機が解ける）。
            using (ct.Register(() => output.Stop()))
            {
                await finished.Task.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested(); // キャンセルは OperationCanceledException として伝播
        }
    }
}
