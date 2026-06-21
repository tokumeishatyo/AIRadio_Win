namespace AIRadio.Core;

/// <summary>
/// 直近に再生した曲の演奏済みリング（最大 <see cref="_capacity"/>・FIFO・スレッドセーフ。W-DEDUP）。
/// 判定は trackURI 完全一致。<see cref="TrackInfo.Title"/> / <see cref="TrackInfo.Artist"/> も保持し、
/// 将来の正規化キー併用・「避けて」ヒントの余地を残す（保持メタは first-wins＝同一 URI を別メタで再投入しても初回を保持）。
/// <para>選曲の採否は原子的な <see cref="TryReserve"/> を使う（判定＋記録を 1 ロックで行い、ローリング先読み窓 2 の
/// 並行選曲で同じ曲を二重採用する TOCTOU 競合を防ぐ＝CONC-1）。<see cref="IsRecent"/> は読み取り専用（テスト/観察用）。</para>
/// 実差し替え境界ではない純粋ドメイン型ゆえ interface 化しない（§3-5）。公開面は ctor / <see cref="IsRecent"/> /
/// <see cref="TryReserve"/> のみ（件数読み出し等の test-only API は足さない＝OBS-7）。
/// </summary>
public sealed class PlayedSongHistory
{
    private readonly int _capacity;
    private readonly LinkedList<TrackInfo> _items = new();          // 先頭＝最古（FIFO で先頭を破棄）
    private readonly HashSet<string> _uris = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <param name="capacity">覚えておく直近曲数（既定 100）。0 で無効（常に採用可・記録しない）。負値は 0 にクランプ。</param>
    public PlayedSongHistory(int capacity = 100) => _capacity = Math.Max(0, capacity);  // 負値 → 0（無効）

    /// <summary>読み取り専用（テスト/観察用）。capacity 0（無効）・空 URI は常に false。</summary>
    public bool IsRecent(string uri)
    {
        if (_capacity == 0 || string.IsNullOrEmpty(uri))
        {
            return false;
        }
        lock (_lock)
        {
            return _uris.Contains(uri);
        }
    }

    /// <summary>
    /// 原子的な「判定＋予約」。既出なら <c>false</c>（メタは初回固定＝first-wins・上書きしない）。
    /// 未既出なら追加し（上限超過時は FIFO で最古を破棄）<c>true</c> を返す。選曲の採否はこれを使う（TOCTOU 回避＝CONC-1）。
    /// capacity 0（無効）・空 URI は判定せず <c>true</c>（採用可・記録しない）。
    /// </summary>
    public bool TryReserve(TrackInfo track)
    {
        if (_capacity == 0 || string.IsNullOrEmpty(track.Uri))
        {
            return true;
        }
        lock (_lock)
        {
            if (!_uris.Add(track.Uri))
            {
                return false;          // 既出＝予約失敗（保持メタは上書きしない）
            }
            _items.AddLast(track);
            if (_items.Count > _capacity)
            {
                var oldest = _items.First!.Value;   // FIFO で最古を破棄
                _items.RemoveFirst();
                _uris.Remove(oldest.Uri);
            }
            return true;
        }
    }
}
