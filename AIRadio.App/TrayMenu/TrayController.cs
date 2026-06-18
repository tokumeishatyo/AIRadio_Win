using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using AIRadio.App.Composition;
using AIRadio.Core;
using AIRadio.Infrastructure;

namespace AIRadio.App;

/// <summary>
/// タスクトレイのアイコン + メニュー（放送を開始⇄停止 / ED で終了 / 番組の長さ▸（10/20/30 本）/ 終了）。
/// Mac 版 `MenuBarController` の移植（W9 + W13）。<see cref="BroadcastSession"/> を保持し、状態変化を
/// <see cref="Dispatcher.UIThread"/> 経由でメニューラベル・活性へ反映する。
/// </summary>
internal sealed class TrayController
{
    /// <summary>メニュー「番組の長さ」の選択肢（仕様 w13 §6-2。エンドレスは UI 非露出）。</summary>
    private static readonly ProgramLength[] LengthChoices =
    {
        ProgramLength.FromCorners(10), ProgramLength.FromCorners(20), ProgramLength.FromCorners(30),
    };

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IRadioLog _log = new ConsoleRadioLog();
    private readonly BroadcastSession _session;
    private readonly BroadcastComposition _broadcast;
    private readonly string _configDir;
    private readonly ProgramLengthStore _lengthStore = new();
    private readonly List<(ProgramLength Choice, NativeMenuItem Item)> _lengthItems = new();

    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _stateItem = new("停止中") { IsEnabled = false };
    private readonly NativeMenuItem _toggleItem = new("放送を開始");
    private readonly NativeMenuItem _endingItem = new("ED で終了") { IsEnabled = false };
    private readonly NativeMenuItem _generateArtistsItem = new("アーティスト一覧を生成");

    /// <summary>放送中の操作（「ED で終了」）。開始時にセット、Idle で破棄（UI スレッドのみ触る）。</summary>
    private BroadcastControl? _activeControl;

    // アーティスト一覧生成（W15 §9-3）の状態は App 側で持つ（BroadcastSession は拡張しない＝§18-13）。すべて UI スレッドのみが触る。
    private bool _isBroadcasting;
    private bool _isGeneratingArtists;
    private Task? _generateTask;
    private CancellationTokenSource? _generateCts;

    /// <summary>終了処理（明示「終了」 or セッション終了）に入ったか。二重 graceful shutdown を防ぐ（W-Win §4）。</summary>
    private bool _isShuttingDown;

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _configDir = Path.Combine(AppContext.BaseDirectory, "config");
        _broadcast = new BroadcastComposition(_configDir, _log);
        _session = new BroadcastSession(OnStateChanged);

        _toggleItem.Click += (_, _) => Toggle();
        _endingItem.Click += (_, _) => RequestEnding();
        _generateArtistsItem.Click += (_, _) => GenerateArtists();
        var quitItem = new NativeMenuItem("終了");
        quitItem.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_stateItem);
        menu.Add(_toggleItem);
        menu.Add(_endingItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(BuildLengthMenuItem());
        menu.Add(_generateArtistsItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "ケイラボAIラジオ",
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

        // セッション終了（ログオフ/シャットダウン）の best-effort graceful 停止（W-Win §4）。
        // 明示「終了」は Shutdown(0) 直叩きで本イベントを発火しないため二重処理にならない（TryShutdown のみ発火）。
        _desktop.ShutdownRequested += OnShutdownRequested;

        // VOICEVOX 自動起動（W-Win §3）。アプリ起動時に未応答なら起動。完全 fail-tolerant（背景・throw しない）。
        _ = EnsureVoicevoxAsync();

        _log.Log("ケイラボAIラジオ — タスクトレイに常駐します（アイコンのメニューから開始 / 停止）。");
    }

    /// <summary>アプリ起動時に VOICEVOX が未応答なら起動する（W-Win §3）。結果をログに出すだけ＝放送系には無影響。</summary>
    private async Task EnsureVoicevoxAsync()
    {
        try
        {
            var tts = TtsConfig.LoadFile(Path.Combine(_configDir, "tts.yaml"));
            var launcher = new VoicevoxLauncher(
                tts.Endpoint, new HttpClientAdapter(), VoicevoxLauncher.ResolveExePath(tts.VoicevoxExePath));
            var result = await launcher.EnsureRunningAsync().ConfigureAwait(false);
            _log.Log(result switch
            {
                VoicevoxLaunchResult.Launched => "VOICEVOX を自動起動しました。",
                VoicevoxLaunchResult.AlreadyRunning => "VOICEVOX は起動済みです。",
                VoicevoxLaunchResult.NotConfigured =>
                    "VOICEVOX が見つかりませんでした（手動で起動してください。tts.yaml の voicevox.exe_path で明示できます）。",
                VoicevoxLaunchResult.LaunchFailed => "VOICEVOX の自動起動に失敗しました（手動で起動してください）。",
                _ => "",
            });
        }
        catch (Exception ex)
        {
            // tts.yaml ロード失敗等。放送系は各々が再ロードするため無影響（自動起動だけスキップ）。
            _log.Log($"VOICEVOX 自動起動をスキップしました: {ex.Message}");
        }
    }

    /// <summary>
    /// OS のセッション終了（ログオフ/シャットダウン）。放送中なら best-effort で停止＋Spotify pause（完全静寂 §3-1）。
    /// OS は数秒で kill するため bounded。<see cref="ShutdownRequestedEventArgs.Cancel"/> は立てない（終了を妨げない）。
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }
        _isShuttingDown = true;
        try
        {
            // Task.Run で UI 同期コンテキストを外し、UI スレッドブロックでの再入デッドロックを避ける。
            Task.Run(() => _session.StopAndWaitAsync()).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            /* best-effort: セッション終了は妨げない */
        }
    }

    private void Toggle()
    {
        if (_isGeneratingArtists)
        {
            return; // 生成中は放送開始を受け付けない（相互排他。§9-3。ボタンも無効化済みだが二重ガード）。
        }
        if (_session.CurrentState == BroadcastSession.State.Broadcasting)
        {
            _session.Stop();
        }
        else
        {
            var control = new BroadcastControl();
            _activeControl = control;
            _session.Start(ct => _broadcast.RunAsync(ct, control));
        }
    }

    /// <summary>
    /// 「アーティスト一覧を生成」（W15 §9-3）。放送停止時のみ。LLM 生成 + Spotify 実在検証で artists.yaml を原子的に上書き。
    /// 背景 Task で実行し、UI 反映は <see cref="Dispatcher.UIThread"/> へマーシャル。生成と放送は相互排他。
    /// </summary>
    private void GenerateArtists()
    {
        if (_isBroadcasting || _isGeneratingArtists)
        {
            return;
        }
        _isGeneratingArtists = true;
        _stateItem.Header = "アーティスト生成中…";
        _generateArtistsItem.Header = "アーティスト一覧を生成中…";
        RefreshMenuEnablement();

        var cts = new CancellationTokenSource();
        _generateCts = cts;
        _generateTask = Task.Run(async () =>
        {
            try
            {
                var count = await _broadcast.GenerateArtistListAsync(cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => FinishGenerating($"アーティスト一覧を生成しました（{count} 組）。"));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => FinishGenerating("アーティスト生成を中止しました。"));
            }
            catch (Exception ex)
            {
                var detail = ex is IRadioError re ? $"生成エラー [{re.Code}]: {ex.Message}" : $"生成エラー: {ex.Message}";
                _log.Log(detail);
                Dispatcher.UIThread.Post(() => FinishGenerating("アーティスト生成に失敗しました（artists.yaml は変更なし）。"));
            }
        });
    }

    /// <summary>生成の完了/失敗/中止の後始末（UI スレッド）。状態を戻し、メニュー活性を更新する。</summary>
    private void FinishGenerating(string message)
    {
        _isGeneratingArtists = false;
        _generateCts?.Dispose();
        _generateCts = null;
        _generateTask = null;
        _generateArtistsItem.Header = "アーティスト一覧を生成";
        _stateItem.Header = _isBroadcasting ? "放送中…" : "停止中";
        _log.Log(message);
        RefreshMenuEnablement();
    }

    /// <summary>
    /// メニュー活性を命令的に更新（Avalonia に validateMenuItem 相当がないため＝§18-14）。
    /// 生成ボタン: 放送停止 かつ 非生成中のみ有効。放送開始: 放送中 または 非生成中のみ有効（生成中は無効＝相互排他）。
    /// </summary>
    private void RefreshMenuEnablement()
    {
        _generateArtistsItem.IsEnabled = !_isBroadcasting && !_isGeneratingArtists;
        _toggleItem.IsEnabled = _isBroadcasting || !_isGeneratingArtists;
    }

    /// <summary>「ED で終了」: 現セグメント（+ 準備済みの直後トーク）を流したら ED で締める（仕様 w13 §3-4）。</summary>
    private void RequestEnding()
    {
        _activeControl?.RequestEnding();
        _stateItem.Header = "放送中…（ED で終了します）"; // Mac requestEnding() 同様に楽観的更新。
    }

    /// <summary>サブメニュー「番組の長さ」（トーク 10/20/30 本）。選択は %LOCALAPPDATA% 永続化、次の放送開始から反映。</summary>
    private NativeMenuItem BuildLengthMenuItem()
    {
        var lengthMenu = new NativeMenu();
        foreach (var choice in LengthChoices)
        {
            var item = new NativeMenuItem(LengthLabel(choice)) { ToggleType = MenuItemToggleType.Radio };
            var captured = choice;
            item.Click += (_, _) => SelectLength(captured);
            _lengthItems.Add((choice, item));
            lengthMenu.Add(item);
        }
        var root = new NativeMenuItem("番組の長さ") { Menu = lengthMenu };
        RefreshLengthChecks();
        return root;
    }

    private static string LengthLabel(ProgramLength length)
        => length.IsEndless ? "エンドレス" : $"トーク {length.Corners} 本";

    private void SelectLength(ProgramLength choice)
    {
        _lengthStore.Write(choice);
        RefreshLengthChecks();
    }

    /// <summary>現在値（store ?? program.yaml の既定）に応じてラジオチェックを更新。該当なしはどれもチェックしない。</summary>
    private void RefreshLengthChecks()
    {
        var current = _lengthStore.Read() ?? DefaultLength();
        foreach (var (choice, item) in _lengthItems)
        {
            item.IsChecked = choice == current;
        }
    }

    private ProgramLength DefaultLength()
    {
        try { return ProgramConfig.LoadFile(Path.Combine(_configDir, "program.yaml")).DefaultLength; }
        catch { return ProgramLength.FromCorners(10); }
    }

    private void Quit() => _ = ShutdownAsync();

    /// <summary>graceful shutdown: 放送停止 → 後始末 pause 完了待ち → 終了（鳴らしっぱなし防止 §3-1）。</summary>
    private async Task ShutdownAsync()
    {
        _isShuttingDown = true; // セッション終了ハンドラとの二重処理を防ぐ（W-Win §4）。
        // 進行中のアーティスト生成があればキャンセルして待つ（一時ファイルを残さない・rename とシャットダウンの競合回避。§9-3）。
        _generateCts?.Cancel();
        var generateTask = _generateTask;
        if (generateTask is not null)
        {
            try { await generateTask.ConfigureAwait(false); } catch { /* 中止/失敗は無視（best-effort） */ }
        }
        await _session.StopAndWaitAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _trayIcon.IsVisible = false;
            _desktop.Shutdown(0);
        });
    }

    // BroadcastSession の lock 内から呼ばれるため、UI スレッドへ非ブロッキングにマーシャルする。
    private void OnStateChanged(BroadcastSession.State state)
        => Dispatcher.UIThread.Post(() => Apply(state));

    private void Apply(BroadcastSession.State state)
    {
        switch (state)
        {
            case BroadcastSession.State.Broadcasting:
                _isBroadcasting = true;
                _toggleItem.Header = "放送を停止";
                _stateItem.Header = "放送中…";
                _endingItem.IsEnabled = true;
                break;
            case BroadcastSession.State.Idle:
                _isBroadcasting = false;
                _toggleItem.Header = "放送を開始";
                _stateItem.Header = "停止中";
                _endingItem.IsEnabled = false;
                _activeControl = null; // 自走完走・停止のいずれでも確実に破棄（唯一の権威ある状態コールバック）。
                break;
        }
        RefreshMenuEnablement();
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://AIRadio.App/Assets/tray.ico"));
        return new WindowIcon(stream);
    }
}
