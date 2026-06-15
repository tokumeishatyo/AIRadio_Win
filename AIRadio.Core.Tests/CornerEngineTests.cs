using System.Text;
using AIRadio.Core;
using AIRadio.TestSupport;

namespace AIRadio.Core.Tests;

public class CornerEngineTests
{
    private static readonly IReadOnlyList<DjProfile> Djs = new[]
    {
        new DjProfile("zundamon", "ずんだもん", 3, "語尾は〜なのだ"),
        new DjProfile("metan", "四国めたん", 2, "上品な口調"),
    };

    private static CornerTemplate FreeTalkCorner(int playSeconds = 30) => new(
        "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
        new[] { "zundamon", "metan" }, "spotify:track:fallback",
        Volume: 100, PlaySeconds: playSeconds);

    private const string ScriptResponse =
        "ずんだもん: こんにちは、なのだ。\n" +
        "四国めたん: どうも。\n" +
        "ずんだもん: 今日のテーマは音楽なのだ。\n" +
        "四国めたん: いいですわね。";

    [Fact]
    public async Task RunAsync_FreeTalk_PicksSong_SpeaksScript_PlaysSong_ThenPauses()
    {
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var spotify = new FakeSpotifyController();
        var audio = new SpyAudioPlayer();
        var events = new List<CornerEvent>();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), audio, searcher, spotify, new FakeClock(),
            onEvent: e => { lock (events) { events.Add(e); } });

        await engine.RunAsync(FreeTalkCorner(), Djs);

        Assert.Equal(4, audio.Played.Count); // 4 行発話
        // 行ごとに正しい話者へルーティングされている（InMemoryTTS は "{speakerId}:{text}" を返す）。
        // 台本は ずんだもん(3) / 四国めたん(2) の交互。
        var played = audio.Played.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.StartsWith("3:", played[0]); // ずんだもん → speaker 3
        Assert.StartsWith("2:", played[1]); // 四国めたん → speaker 2
        Assert.StartsWith("3:", played[2]);
        Assert.StartsWith("2:", played[3]);
        Assert.Contains(new SpotifyEvent.Play("spotify:track:idol"), spotify.Events);
        Assert.Contains(new SpotifyEvent.SetVolume(100), spotify.Events);
        Assert.Equal(new SpotifyEvent.Pause(), spotify.Events.Last()); // 完全静寂

        lock (events)
        {
            Assert.Contains(events, e => e is CornerEvent.SongPicked sp && sp.Track.Uri == "spotify:track:idol");
            Assert.Contains(events, e => e is CornerEvent.ScriptReady sr && sr.LineCount == 4);
            Assert.Equal(4, events.Count(e => e is CornerEvent.Line));
        }
    }

    [Fact]
    public async Task PrepareAsync_NonFreeTalk_ThrowsConfigException()
    {
        var engine = new CornerEngine(
            new ScriptedLLM(), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeTrackSearcher(), new FakeSpotifyController(), new FakeClock());
        var corner = new CornerTemplate(
            "letter", "お便り", "x", CornerFormat.Letter, new[] { "zundamon" }, "spotify:track:fb");

        await Assert.ThrowsAsync<ConfigException>(() => engine.PrepareAsync(corner, Djs));
    }

    [Fact]
    public async Task PrepareAsync_UnknownDj_ThrowsConfigException()
    {
        var engine = new CornerEngine(
            new ScriptedLLM("アイドル - YOASOBI", ScriptResponse), new InMemoryTTS(), new SpyAudioPlayer(),
            new FakeTrackSearcher(new[] { new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI") }),
            new FakeSpotifyController(), new FakeClock());
        var corner = new CornerTemplate(
            "free_talk", "フリートーク", "音楽", CornerFormat.FreeTalk,
            new[] { "unknown_dj" }, "spotify:track:fb");

        await Assert.ThrowsAsync<ConfigException>(() => engine.PrepareAsync(corner, Djs));
    }

    [Fact]
    public async Task RunAsync_PausesEvenWhenPlaybackFails()
    {
        // prepare は audio を使わない（tts のみ）→ 成功。run の発話再生で失敗 → 完全静寂を保証。
        var llm = new ScriptedLLM("アイドル - YOASOBI", ScriptResponse);
        var searcher = new FakeTrackSearcher(new[]
        {
            new TrackInfo("spotify:track:idol", "アイドル", "YOASOBI", IsPlayable: true),
        });
        var spotify = new FakeSpotifyController();
        var engine = new CornerEngine(
            llm, new InMemoryTTS(), new ThrowingAudioPlayer(), searcher, spotify, new FakeClock());

        await Assert.ThrowsAsync<AudioException>(() => engine.RunAsync(FreeTalkCorner(), Djs));

        Assert.Contains(new SpotifyEvent.Pause(), spotify.Events);            // §3-1 完全静寂
        Assert.Equal(new SpotifyEvent.SetVolume(100), spotify.Events.Last()); // ダッキング相当の音量復元
    }

    private sealed class ThrowingAudioPlayer : IAudioPlayer
    {
        public Task PlayAsync(byte[] wav, CancellationToken ct = default) => throw AudioException.PlaybackFailed();
    }
}
