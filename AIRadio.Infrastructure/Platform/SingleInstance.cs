namespace AIRadio.Infrastructure;

/// <summary>
/// 名前付き <see cref="Mutex"/> による単一インスタンス検出（W-Win §2）。Mutex の<b>存在</b>で多重起動を判定する（所有はしない）。
/// 1 個目は <c>createdNew=true</c>、2 個目以降は false。プロセス寿命の間オブジェクトを保持し、終了時に OS がハンドルを閉じて名前を破棄する。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    /// <summary>このインスタンスが最初の起動か（true=唯一・false=既に他インスタンスが存在）。</summary>
    public bool IsFirstInstance { get; }

    public SingleInstance(string name)
    {
        // 所有せず（initiallyOwned: false）名前付き Mutex を作る。createdNew が false なら他インスタンスが先在。
        _mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose() => _mutex.Dispose();
}
