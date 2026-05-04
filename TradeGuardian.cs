using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace TradeGuardian;

/// <summary>
/// Step 1 scaffold for Trade Guardian + Revenge Guard.
/// This version focuses only on base strategy structure and open-position detection.
/// </summary>
public sealed class TradeGuardian : Strategy
{
    public enum EvaluationMode
    {
        BarClose = 0,
        Intrabar = 1
    }

    public enum ExitInvalidationMode
    {
        PriceCrossesEma9 = 0,
        PriceCrossesEma21 = 1,
        Ema9CrossesEma21 = 2,
        CandleCloseBeyondEma21 = 3,
        CandleCloseBeyondBothEmas = 4
    }

    public enum BeyondEmaTriggerMode
    {
        BarClose = 0,
        BarCrossover = 1
    }

    [InputParameter("Symbol")]
    public Symbol Symbol { get; set; }

    [InputParameter("Account")]
    public Account Account { get; set; }

    [InputParameter("Filter by selected symbol", 10)]
    public bool FilterBySelectedSymbol { get; set; } = true;

    [InputParameter("Filter by selected account", 20)]
    public bool FilterBySelectedAccount { get; set; } = true;

    [InputParameter("Account names CSV (optional)", 21)]
    public string AccountNamesCsv { get; set; } = string.Empty;

    [InputParameter("Evaluation mode", 30)]
    public EvaluationMode UpdateMode { get; set; } = EvaluationMode.BarClose;

    [InputParameter("Timeframe (minutes)", 40, 1, 240, 1, 0)]
    public int TimeframeMinutes { get; set; } = 1;

    [InputParameter("History lookback days", 50, 1, 30, 1, 0)]
    public int HistoryLookbackDays { get; set; } = 5;

    [InputParameter("Verbose EMA logs", 60)]
    public bool VerboseEmaLogs { get; set; } = false;

    [InputParameter("Trade Guardian enabled", 70)]
    public bool TradeGuardianEnabled { get; set; } = true;

    [InputParameter("Exit invalidation mode", 80)]
    public ExitInvalidationMode InvalidationMode { get; set; } = ExitInvalidationMode.CandleCloseBeyondEma21;

    [InputParameter("Enable sound alert", 90)]
    public bool EnableSoundAlert { get; set; } = true;

    [InputParameter("Sound repetitions", 100, 1, 5, 1, 0)]
    public int SoundRepetitions { get; set; } = 2;

    [InputParameter("Auto-close position on trigger", 110)]
    public bool AutoClosePositionOnTrigger { get; set; } = false;

    [InputParameter("Revenge Guard enabled", 120)]
    public bool RevengeGuardEnabled { get; set; } = true;

    [InputParameter("Use account-wide PnL for Revenge Guard", 121)]
    public bool UseAccountWidePnlForRevengeGuard { get; set; } = true;

    [InputParameter("Daily loss limit", 130, -50000, 0, 10, 2)]
    public double DailyLossLimit { get; set; } = -150;

    [InputParameter("Giveback trigger from high", 140, 1, 50000, 10, 2)]
    public double GivebackTrigger { get; set; } = 100;

    [InputParameter("Use net-loss threshold for Revenge Mode", 141)]
    public bool UseNetLossThresholdForRevengeMode { get; set; } = true;

    [InputParameter("Lockout duration (minutes)", 150, 1, 240, 1, 0)]
    public int LockoutDurationMinutes { get; set; } = 10;

    [InputParameter("Disable new trades on lockout", 160)]
    public bool DisableNewTradesOnLockout { get; set; } = true;

    [InputParameter("Auto-close on daily hard stop", 170)]
    public bool AutoCloseOnDailyHardStop { get; set; } = false;

    [InputParameter("Beyond EMA trigger mode", 175)]
    public BeyondEmaTriggerMode BeyondTriggerMode { get; set; } = BeyondEmaTriggerMode.BarCrossover;

    [InputParameter("Use bar-close confirmation for beyond EMA", 176)]
    public bool UseBarCloseConfirmationForBeyondEma { get; set; } = false;

    [InputParameter("Show alert status in metrics", 180)]
    public bool ShowAlertStatusInMetrics { get; set; } = true;

    private readonly HashSet<Position> trackedPositions = new();
    private Position currentOpenPosition;
    private string lastStatusMessage = "Flat";
    private HistoricalData history;
    private long lastProcessedBarRightTicks = -1;
    private double ema9;
    private double ema21;
    private double previousEma9;
    private double previousEma21;
    private double referenceClose;
    private double referenceHigh;
    private double referenceLow;
    private double previousReferenceClose;
    private bool hasPreviousEmaSnapshot;
    private string lastGuardianTriggerKey = string.Empty;
    private DateTime sessionDateLocal;
    private double realizedPnlToday;
    private double unrealizedPnl;
    private double totalPnl;
    private double highOfDayPnl;
    private DateTime? revengeModeUntilLocal;
    private bool hardStopForDay;
    private string lastRevengeTriggerKey = string.Empty;
    private bool previousTradeBlockedState;
    private AlertBannerState currentBanner = AlertBannerState.None;
    private HashSet<string> scopedAccountNames = new(StringComparer.OrdinalIgnoreCase);

    public TradeGuardian()
    {
        Name = "Trade Guardian + Revenge Guard";
        Description = "Discipline enforcement strategy scaffold (steps 1-5).";
    }

    protected override void OnRun()
    {
        Log("Strategy started. Initializing position and EMA watchers.", StrategyLoggingLevel.Info);

        if (Symbol == null)
        {
            Log("Symbol input is required.", StrategyLoggingLevel.Error);
            return;
        }

        RefreshAccountScope();
        if (FilterBySelectedAccount && !HasAccountScope())
            Log("Account filter is enabled but no account scope is configured. Set Account or Account names CSV.", StrategyLoggingLevel.Error);

        Core.Instance.PositionAdded += OnPositionAdded;
        Core.Instance.PositionRemoved += OnPositionRemoved;
        Core.Instance.ClosedPositionAdded += OnClosedPositionChanged;
        Core.Instance.ClosedPositionRemoved += OnClosedPositionChanged;
        Core.Instance.OrderAdded += OnOrderAdded;

        foreach (var position in Core.Instance.Positions)
            SubscribeToPosition(position);

        ResetSessionState(DateTime.Now.Date);
        RecalculateRealizedPnl("OnRun");
        RecalculateCombinedPnlAndGuards("OnRun");

        InitializeHistory();
        Symbol.NewLast += OnSymbolNewLast;

        RefreshOpenPositionState("OnRun");
        RefreshEmaState("OnRun", forceLog: true);
    }

    protected override void OnStop()
    {
        if (Symbol != null)
            Symbol.NewLast -= OnSymbolNewLast;

        Core.Instance.PositionAdded -= OnPositionAdded;
        Core.Instance.PositionRemoved -= OnPositionRemoved;
        Core.Instance.ClosedPositionAdded -= OnClosedPositionChanged;
        Core.Instance.ClosedPositionRemoved -= OnClosedPositionChanged;
        Core.Instance.OrderAdded -= OnOrderAdded;

        foreach (var position in trackedPositions.ToList())
            UnsubscribeFromPosition(position);

        trackedPositions.Clear();
        currentOpenPosition = null;
        lastStatusMessage = "Stopped";
        history = null;
        lastProcessedBarRightTicks = -1;
        hasPreviousEmaSnapshot = false;
        lastGuardianTriggerKey = string.Empty;
        lastRevengeTriggerKey = string.Empty;
        revengeModeUntilLocal = null;
        hardStopForDay = false;
        previousTradeBlockedState = false;
        currentBanner = AlertBannerState.None;

        Log("Strategy stopped. Position subscriptions removed.", StrategyLoggingLevel.Info);
    }

    private void OnPositionAdded(Position position)
    {
        SubscribeToPosition(position);
        RefreshOpenPositionState("PositionAdded");
    }

    private void OnPositionRemoved(Position position)
    {
        UnsubscribeFromPosition(position);
        RefreshOpenPositionState("PositionRemoved");
    }

    private void OnPositionUpdated(Position position)
    {
        if (!MatchesScope(position))
            return;

        RefreshOpenPositionState("PositionUpdated");
        RecalculateCombinedPnlAndGuards("PositionUpdated");
    }

    private void SubscribeToPosition(Position position)
    {
        if (position == null || trackedPositions.Contains(position))
            return;

        trackedPositions.Add(position);
        position.Updated += OnPositionUpdated;
    }

    private void UnsubscribeFromPosition(Position position)
    {
        if (position == null || !trackedPositions.Contains(position))
            return;

        position.Updated -= OnPositionUpdated;
        trackedPositions.Remove(position);
    }

    private void RefreshOpenPositionState(string source)
    {
        var nextPosition = Core.Instance.Positions.FirstOrDefault(MatchesScope);
        var previousPositionId = currentOpenPosition?.Id;
        var nextPositionId = nextPosition?.Id;

        if (previousPositionId == nextPositionId)
            return;

        currentOpenPosition = nextPosition;

        if (currentOpenPosition == null)
        {
            lastStatusMessage = "Flat";
            lastGuardianTriggerKey = string.Empty;
            Log($"[{source}] No matching open position (Flat).", StrategyLoggingLevel.Info);
            return;
        }

        var direction = DetectDirection(currentOpenPosition);
        lastStatusMessage = $"{direction} {currentOpenPosition.Quantity:0.####}";
        Log(
            $"[{source}] Open position detected: {direction}, Qty={currentOpenPosition.Quantity:0.####}, Symbol={currentOpenPosition.Symbol?.Name}, Account={currentOpenPosition.Account?.Name}",
            StrategyLoggingLevel.Info);
        lastGuardianTriggerKey = string.Empty;
    }

    private void OnSymbolNewLast(Symbol symbol, Last last)
    {
        if (history == null || history.Count <= 0)
            return;

        EnsureSessionDate(DateTime.Now);
        RecalculateCombinedPnlAndGuards("NewLast");

        if (UpdateMode == EvaluationMode.Intrabar)
        {
            RefreshEmaState("NewLast");
            EvaluateTradeGuardian("NewLast");
            return;
        }

        var currentBar = history[0] as HistoryItemBar;
        if (currentBar == null)
            return;

        if (currentBar.TicksRight == lastProcessedBarRightTicks)
            return;

        lastProcessedBarRightTicks = currentBar.TicksRight;
        RefreshEmaState("BarClose");
        EvaluateTradeGuardian("BarClose");
    }

    private void OnClosedPositionChanged(object _)
    {
        EnsureSessionDate(DateTime.Now);
        RecalculateRealizedPnl("ClosedPositionChanged");
        RecalculateCombinedPnlAndGuards("ClosedPositionChanged");
    }

    private void OnOrderAdded(Order order)
    {
        if (order == null || !IsOrderInScope(order))
            return;

        if (!IsNewTradeBlocked(DateTime.Now))
            return;

        try
        {
            var result = Core.Instance.CancelOrder((IOrder)order);
            var statusText = result?.Status.ToString() ?? "Unknown";
            var messageText = result?.Message ?? string.Empty;

            Log(
                $"[OrderAdded] Blocked and canceled new order: Id={order.Id}, Side={order.Side}, Qty={order.TotalQuantity:0.####}, Status={statusText}, Message={messageText}",
                StrategyLoggingLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"[OrderAdded] Failed to cancel blocked order {order.Id}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void InitializeHistory()
    {
        var period = new Period(BasePeriod.Minute, Math.Max(1, TimeframeMinutes));
        var fromTime = DateTime.UtcNow.AddDays(-Math.Max(1, HistoryLookbackDays));
        history = Symbol.GetHistory(period, fromTime);

        if (history == null)
        {
            Log("Failed to initialize historical data for EMA calculations.", StrategyLoggingLevel.Error);
            return;
        }

        if (history.Count > 0)
        {
            var currentBar = history[0] as HistoryItemBar;
            if (currentBar != null)
                lastProcessedBarRightTicks = currentBar.TicksRight;
        }
        else
        {
            Log("EMA history initialized but currently empty. Waiting for first bar.", StrategyLoggingLevel.Info);
        }

        Log(
            $"EMA history initialized: Symbol={Symbol.Name}, TF={TimeframeMinutes}m, Mode={UpdateMode}, Bars={history.Count}",
            StrategyLoggingLevel.Info);
    }

    private void RefreshEmaState(string source, bool forceLog = false)
    {
        if (history == null || history.Count < 25)
            return;

        var includeCurrentBar = UpdateMode == EvaluationMode.Intrabar;
        var snapshot = BuildEmaSnapshot(includeCurrentBar);
        if (!snapshot.IsValid)
            return;

        if (hasPreviousEmaSnapshot)
        {
            previousEma9 = ema9;
            previousEma21 = ema21;
            previousReferenceClose = referenceClose;
        }

        ema9 = snapshot.Ema9;
        ema21 = snapshot.Ema21;
        referenceClose = snapshot.ReferenceClose;
        referenceHigh = snapshot.ReferenceHigh;
        referenceLow = snapshot.ReferenceLow;
        hasPreviousEmaSnapshot = true;

        if (forceLog || VerboseEmaLogs)
        {
            Log(
                $"[{source}] EMA9={ema9:0.00}, EMA21={ema21:0.00}, RefClose={referenceClose:0.00}, RefHigh={referenceHigh:0.00}, RefLow={referenceLow:0.00}, UseCurrentBar={includeCurrentBar}",
                StrategyLoggingLevel.Info);
        }
    }

    private void EvaluateTradeGuardian(string source)
    {
        if (!TradeGuardianEnabled || currentOpenPosition == null || !hasPreviousEmaSnapshot)
            return;

        var positionDirection = GetPositionDirection(currentOpenPosition);
        if (positionDirection == PositionDirection.Unknown)
            return;

        if (!TryCheckInvalidation(positionDirection, out var reason))
        {
            lastGuardianTriggerKey = string.Empty;
            return;
        }

        var triggerKey = $"{currentOpenPosition.Id}|{InvalidationMode}|{reason}";
        if (triggerKey == lastGuardianTriggerKey)
            return;

        lastGuardianTriggerKey = triggerKey;
        var direction = DetectDirection(currentOpenPosition);
        lastStatusMessage = $"GUARDIAN TRIGGERED ({direction})";
        ActivateBanner(AlertBannerType.Guardian, reason, DateTime.Now.AddSeconds(15));

        Log(
            $"[{source}] TRADE GUARDIAN TRIGGERED: {reason}. Position={direction} Qty={currentOpenPosition.Quantity:0.####} RefClose={referenceClose:0.00} EMA9={ema9:0.00} EMA21={ema21:0.00}",
            StrategyLoggingLevel.Error);

        TryPlayAlertSound(source);
        TryAutoClosePosition(source, reason);
    }

    private bool TryCheckInvalidation(PositionDirection direction, out string reason)
    {
        reason = string.Empty;
        bool isLong = direction == PositionDirection.Long;
        bool isShort = direction == PositionDirection.Short;

        switch (InvalidationMode)
        {
            case ExitInvalidationMode.PriceCrossesEma9:
            {
                bool crossed = isLong
                    ? previousReferenceClose >= previousEma9 && referenceClose < ema9
                    : previousReferenceClose <= previousEma9 && referenceClose > ema9;

                if (!crossed)
                    return false;

                reason = isLong
                    ? "Price crossed below EMA 9"
                    : "Price crossed above EMA 9 (short invalidation)";
                return true;
            }
            case ExitInvalidationMode.PriceCrossesEma21:
            {
                bool crossed = isLong
                    ? previousReferenceClose >= previousEma21 && referenceClose < ema21
                    : previousReferenceClose <= previousEma21 && referenceClose > ema21;

                if (!crossed)
                    return false;

                reason = isLong
                    ? "Price crossed below EMA 21"
                    : "Price crossed above EMA 21 (short invalidation)";
                return true;
            }
            case ExitInvalidationMode.Ema9CrossesEma21:
            {
                bool crossed = isLong
                    ? previousEma9 >= previousEma21 && ema9 < ema21
                    : previousEma9 <= previousEma21 && ema9 > ema21;

                if (!crossed)
                    return false;

                reason = isLong
                    ? "EMA 9 crossed below EMA 21"
                    : "EMA 9 crossed above EMA 21 (short invalidation)";
                return true;
            }
            case ExitInvalidationMode.CandleCloseBeyondEma21:
            {
                bool invalidated = IsBeyondSingleEma(ema21, isLong, isShort);
                if (!invalidated)
                    return false;

                reason = isLong
                    ? BeyondTriggerMode == BeyondEmaTriggerMode.BarClose ? "Candle close beyond EMA 21" : "Price crossed beyond EMA 21"
                    : BeyondTriggerMode == BeyondEmaTriggerMode.BarClose ? "Candle close beyond EMA 21 (short side)" : "Price crossed beyond EMA 21 (short side)";
                return true;
            }
            case ExitInvalidationMode.CandleCloseBeyondBothEmas:
            {
                bool invalidated = IsBeyondBothEmas(isLong, isShort);

                if (!invalidated)
                    return false;

                reason = isLong
                    ? BeyondTriggerMode == BeyondEmaTriggerMode.BarClose
                        ? "Candle close beyond both EMA 9 and EMA 21"
                        : "Price crossed beyond both EMA 9 and EMA 21"
                    : BeyondTriggerMode == BeyondEmaTriggerMode.BarClose
                        ? "Candle close beyond both EMA 9 and EMA 21 (short side)"
                        : "Price crossed beyond both EMA 9 and EMA 21 (short side)";
                return true;
            }
            default:
                return false;
        }
    }

    private EmaSnapshot BuildEmaSnapshot(bool includeCurrentBar)
    {
        if (history == null || history.Count <= 0)
            return EmaSnapshot.Invalid;

        var barsToUse = includeCurrentBar ? history.Count : history.Count - 1;
        if (barsToUse < 21)
            return EmaSnapshot.Invalid;

        // In bar-close mode we must exclude offset 0 (currently forming bar),
        // so invalidation checks are based on fully closed candles only.
        var newestIncludedOffset = includeCurrentBar ? 0 : 1;
        var oldestIncludedOffset = history.Count - 1;
        if (oldestIncludedOffset < newestIncludedOffset)
            return EmaSnapshot.Invalid;

        double nextEma9 = 0;
        double nextEma21 = 0;
        double alpha9 = 2.0 / (9 + 1);
        double alpha21 = 2.0 / (21 + 1);
        bool initialized = false;
        double lastClose = 0;
        double lastHigh = 0;
        double lastLow = 0;

        for (int offset = oldestIncludedOffset; offset >= newestIncludedOffset; offset--)
        {
            if (history[offset] is not HistoryItemBar bar)
                continue;

            var close = bar.Close;
            if (!initialized)
            {
                nextEma9 = close;
                nextEma21 = close;
                initialized = true;
            }
            else
            {
                nextEma9 = (close - nextEma9) * alpha9 + nextEma9;
                nextEma21 = (close - nextEma21) * alpha21 + nextEma21;
            }

            lastClose = close;
            lastHigh = bar.High;
            lastLow = bar.Low;
        }

        return initialized
            ? new EmaSnapshot(nextEma9, nextEma21, lastClose, lastHigh, lastLow, true)
            : EmaSnapshot.Invalid;
    }

    private bool MatchesScope(Position position)
    {
        if (position == null)
            return false;

        if (FilterBySelectedSymbol && Symbol != null && position.Symbol?.Name != Symbol.Name)
            return false;

        if (FilterBySelectedAccount && !IsAccountInScope(position.Account?.Name))
            return false;

        return Math.Abs(position.Quantity) > 0;
    }

    private static string DetectDirection(Position position)
    {
        if (position == null)
            return "Flat";

        if (position.Side == Side.Buy)
            return "Long";

        if (position.Side == Side.Sell)
            return "Short";

        if (position.Quantity > 0)
            return "Long";

        if (position.Quantity < 0)
            return "Short";

        return "Unknown";
    }

    private static bool IsLongPosition(Position position)
    {
        return GetPositionDirection(position) == PositionDirection.Long;
    }

    private static bool IsShortPosition(Position position)
    {
        return GetPositionDirection(position) == PositionDirection.Short;
    }

    private static PositionDirection GetPositionDirection(Position position)
    {
        if (position == null)
            return PositionDirection.Unknown;

        // Prefer broker-reported side. Quantity can be absolute on some connections.
        if (position.Side == Side.Buy)
            return PositionDirection.Long;

        if (position.Side == Side.Sell)
            return PositionDirection.Short;

        if (position.Quantity > 0)
            return PositionDirection.Long;

        if (position.Quantity < 0)
            return PositionDirection.Short;

        return PositionDirection.Unknown;
    }

    private void TryPlayAlertSound(string source)
    {
        if (!EnableSoundAlert)
            return;

        if (!OperatingSystem.IsWindows())
        {
            Log($"[{source}] Sound alert skipped: only supported on Windows.", StrategyLoggingLevel.Info);
            return;
        }

        var repetitions = Math.Max(1, SoundRepetitions);

        try
        {
            for (var i = 0; i < repetitions; i++)
                Console.Beep(1250, 180);
        }
        catch (Exception ex)
        {
            Log($"[{source}] Sound alert failed: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void TryAutoClosePosition(string source, string reason)
    {
        if (!AutoClosePositionOnTrigger || currentOpenPosition == null)
            return;

        var positionToClose = currentOpenPosition;

        try
        {
            positionToClose.Close();
            Log(
                $"[{source}] Auto-close request sent. Reason: {reason}. Position={positionToClose.Id}",
                StrategyLoggingLevel.Trading);
        }
        catch (Exception ex)
        {
            Log(
                $"[{source}] Auto-close failed: {ex.Message}",
                StrategyLoggingLevel.Error);
        }
    }

    private void EnsureSessionDate(DateTime localNow)
    {
        if (sessionDateLocal == default)
        {
            ResetSessionState(localNow.Date);
            return;
        }

        if (sessionDateLocal == localNow.Date)
            return;

        ResetSessionState(localNow.Date);
        RecalculateRealizedPnl("SessionReset");
        RecalculateCombinedPnlAndGuards("SessionReset");
        Log($"Daily session reset. New date: {sessionDateLocal:yyyy-MM-dd}", StrategyLoggingLevel.Info);
    }

    private void ResetSessionState(DateTime date)
    {
        sessionDateLocal = date;
        realizedPnlToday = 0;
        unrealizedPnl = 0;
        totalPnl = 0;
        highOfDayPnl = 0;
        revengeModeUntilLocal = null;
        hardStopForDay = false;
        lastRevengeTriggerKey = string.Empty;
        previousTradeBlockedState = false;
    }

    private void RecalculateRealizedPnl(string source)
    {
        if (!RevengeGuardEnabled)
            return;

        try
        {
            var startOfDay = sessionDateLocal;
            var endOfDay = sessionDateLocal.AddDays(1);
            double realized = 0;

            foreach (var closed in Core.Instance.ClosedPositions)
            {
                if (!ClosedPositionMatchesPnlScope(closed))
                    continue;

                if (!TryGetClosedPositionTime(closed, out var closeTime))
                    continue;

                var localClose = closeTime.Kind == DateTimeKind.Utc ? closeTime.ToLocalTime() : closeTime;
                if (localClose < startOfDay || localClose >= endOfDay)
                    continue;

                realized += GetClosedPositionPnlValue(closed);
            }

            realizedPnlToday = realized;
        }
        catch (Exception ex)
        {
            Log($"[{source}] Failed to recalculate realized PnL: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void RecalculateCombinedPnlAndGuards(string source)
    {
        if (!RevengeGuardEnabled)
            return;

        unrealizedPnl = 0;
        foreach (var position in Core.Instance.Positions)
        {
            if (!MatchesPnlScope(position))
                continue;

            unrealizedPnl += position.NetPnL?.Value ?? 0;
        }

        totalPnl = realizedPnlToday + unrealizedPnl;
        if (totalPnl > highOfDayPnl)
            highOfDayPnl = totalPnl;

        EvaluateRevengeGuard(source);
    }

    private void EvaluateRevengeGuard(string source)
    {
        var now = DateTime.Now;
        bool revengeLockoutActive = revengeModeUntilLocal.HasValue && now < revengeModeUntilLocal.Value;
        if (!revengeLockoutActive)
            revengeModeUntilLocal = null;

        if (hardStopForDay)
        {
            lastStatusMessage = "HARD STOP ACTIVE";
            PublishTradeBlockStateIfChanged(source);
            return;
        }

        var triggerThreshold = Math.Abs(GivebackTrigger);
        var drawdownFromHigh = highOfDayPnl - totalPnl;
        var netLoss = -totalPnl;
        var revengeTriggerDistance = UseNetLossThresholdForRevengeMode ? netLoss : drawdownFromHigh;
        var shouldTriggerRevenge = revengeTriggerDistance >= triggerThreshold;
        if (shouldTriggerRevenge)
            TriggerRevengeMode(source, revengeTriggerDistance, now);

        if (totalPnl <= Math.Min(DailyLossLimit, -Math.Abs(DailyLossLimit)))
            TriggerDailyHardStop(source);

        if (IsRevengeModeActive(now))
            lastStatusMessage = "REVENGE MODE";

        PublishTradeBlockStateIfChanged(source);
    }

    private void TriggerRevengeMode(string source, double triggerDistance, DateTime now)
    {
        var until = now.AddMinutes(Math.Max(1, LockoutDurationMinutes));
        var key = $"{sessionDateLocal:yyyyMMdd}|REVENGE|{UseNetLossThresholdForRevengeMode}|{Math.Round(triggerDistance, 2)}";
        if (lastRevengeTriggerKey == key)
            return;

        revengeModeUntilLocal = until;
        lastRevengeTriggerKey = key;
        lastStatusMessage = "REVENGE MODE";
        ActivateBanner(AlertBannerType.Revenge, "Giveback threshold exceeded", until);

        var modeText = UseNetLossThresholdForRevengeMode ? "NetLossMode" : "DrawdownFromHighMode";
        Log(
            $"[{source}] REVENGE MODE TRIGGERED ({modeText}). TriggerDistance={triggerDistance:0.00}, Threshold={Math.Abs(GivebackTrigger):0.00}, HOD PnL={highOfDayPnl:0.00}, Current PnL={totalPnl:0.00}, LockoutUntil={until:HH:mm:ss}",
            StrategyLoggingLevel.Error);

        TryPlayAlertSound(source);
    }

    private void TriggerDailyHardStop(string source)
    {
        if (hardStopForDay)
            return;

        hardStopForDay = true;
        lastStatusMessage = "DAILY LOSS HARD STOP";
        ActivateBanner(AlertBannerType.HardStop, "Daily loss limit reached", DateTime.MaxValue);

        Log(
            $"[{source}] DAILY HARD STOP TRIGGERED. DailyPnL={totalPnl:0.00}, Limit={DailyLossLimit:0.00}. Trading locked for the day.",
            StrategyLoggingLevel.Error);

        TryPlayAlertSound(source);

        if (AutoCloseOnDailyHardStop)
            TryAutoClosePosition(source, "Daily loss hard stop reached");
    }

    private bool IsRevengeModeActive(DateTime localNow) => revengeModeUntilLocal.HasValue && localNow < revengeModeUntilLocal.Value;

    private bool IsNewTradeBlocked(DateTime localNow)
    {
        if (hardStopForDay)
            return true;

        return DisableNewTradesOnLockout && IsRevengeModeActive(localNow);
    }

    private bool IsOrderInScope(Order order)
    {
        if (order == null)
            return false;

        if (FilterBySelectedSymbol && Symbol != null && order.Symbol?.Name != Symbol.Name)
            return false;

        if (FilterBySelectedAccount && !IsAccountInScope(order.Account?.Name))
            return false;

        return true;
    }

    private bool IsBeyondSingleEma(double emaValue, bool isLong, bool isShort)
    {
        if (GetEffectiveBeyondTriggerMode() == BeyondEmaTriggerMode.BarClose)
            return isLong ? referenceClose < emaValue : isShort && referenceClose > emaValue;

        return isLong ? referenceLow < emaValue : isShort && referenceHigh > emaValue;
    }

    private bool IsBeyondBothEmas(bool isLong, bool isShort)
    {
        if (GetEffectiveBeyondTriggerMode() == BeyondEmaTriggerMode.BarClose)
            return isLong ? referenceClose < ema9 && referenceClose < ema21 : isShort && referenceClose > ema9 && referenceClose > ema21;

        return isLong ? referenceLow < ema9 && referenceLow < ema21 : isShort && referenceHigh > ema9 && referenceHigh > ema21;
    }

    private BeyondEmaTriggerMode GetEffectiveBeyondTriggerMode()
    {
        // Fallback bool is used because some Quantower strategy UIs do not render enum settings reliably.
        return UseBarCloseConfirmationForBeyondEma
            ? BeyondEmaTriggerMode.BarClose
            : BeyondEmaTriggerMode.BarCrossover;
    }

    private void PublishTradeBlockStateIfChanged(string source)
    {
        var isBlocked = IsNewTradeBlocked(DateTime.Now);
        if (isBlocked == previousTradeBlockedState)
            return;

        previousTradeBlockedState = isBlocked;
        if (isBlocked)
        {
            var until = revengeModeUntilLocal.HasValue ? revengeModeUntilLocal.Value.ToString("HH:mm:ss") : "end of day";
            Log($"[{source}] New trades are BLOCKED until {until}.", StrategyLoggingLevel.Error);
        }
        else
        {
            Log($"[{source}] Trade lockout cleared. New trades are allowed.", StrategyLoggingLevel.Info);
        }
    }

    [Obsolete("OnGetMetrics is deprecated by Quantower but kept for Strategy Runner visual status output.")]
    protected override List<StrategyMetric> OnGetMetrics()
    {
        var metrics = new List<StrategyMetric>();
        if (!ShowAlertStatusInMetrics)
            return metrics;

        var now = DateTime.Now;
        var banner = ResolveActiveBanner(now);
        var isBlocked = IsNewTradeBlocked(now);
        var cooldown = revengeModeUntilLocal.HasValue && now < revengeModeUntilLocal.Value
            ? revengeModeUntilLocal.Value.ToString("HH:mm:ss")
            : "-";

        metrics.Add(new StrategyMetric { Name = "Status", FormattedValue = lastStatusMessage });
        metrics.Add(new StrategyMetric { Name = "Alert", FormattedValue = banner.Title });
        metrics.Add(new StrategyMetric { Name = "Alert Details", FormattedValue = string.IsNullOrWhiteSpace(banner.Details) ? "-" : banner.Details });
        metrics.Add(new StrategyMetric { Name = "Realized PnL (Day)", FormattedValue = realizedPnlToday.ToString("0.00") });
        metrics.Add(new StrategyMetric { Name = "Unrealized PnL", FormattedValue = unrealizedPnl.ToString("0.00") });
        metrics.Add(new StrategyMetric { Name = "Total PnL", FormattedValue = totalPnl.ToString("0.00") });
        metrics.Add(new StrategyMetric { Name = "High-of-Day PnL", FormattedValue = highOfDayPnl.ToString("0.00") });
        metrics.Add(new StrategyMetric { Name = "Trades Blocked", FormattedValue = isBlocked ? "YES" : "NO" });
        metrics.Add(new StrategyMetric { Name = "Cooldown Until", FormattedValue = cooldown });
        return metrics;
    }

    private void ActivateBanner(AlertBannerType type, string details, DateTime expiresAtLocal)
    {
        currentBanner = new AlertBannerState(type, details, expiresAtLocal);
    }

    private AlertBannerState ResolveActiveBanner(DateTime localNow)
    {
        if (hardStopForDay)
            return new AlertBannerState(AlertBannerType.HardStop, "Trading locked for today", DateTime.MaxValue);

        if (IsRevengeModeActive(localNow))
        {
            var until = revengeModeUntilLocal?.ToString("HH:mm:ss") ?? "--:--:--";
            return new AlertBannerState(AlertBannerType.Revenge, $"Cooldown until {until}", DateTime.MaxValue);
        }

        if (currentBanner.Type == AlertBannerType.None)
            return AlertBannerState.None;

        if (localNow > currentBanner.ExpiresAtLocal)
        {
            currentBanner = AlertBannerState.None;
            return AlertBannerState.None;
        }

        return currentBanner;
    }

    private bool ClosedPositionMatchesScope(object closedPosition)
    {
        if (closedPosition == null)
            return false;

        if (FilterBySelectedSymbol && Symbol != null)
        {
            var closedSymbolName = GetNestedNameByReflection(closedPosition, "Symbol");
            if (!string.Equals(closedSymbolName, Symbol.Name, StringComparison.Ordinal))
                return false;
        }

        if (FilterBySelectedAccount)
        {
            var closedAccountName = GetNestedNameByReflection(closedPosition, "Account");
            if (!IsAccountInScope(closedAccountName))
                return false;
        }

        return true;
    }

    private bool MatchesPnlScope(Position position)
    {
        if (position == null)
            return false;

        if (UseAccountWidePnlForRevengeGuard)
        {
            if (FilterBySelectedAccount && !IsAccountInScope(position.Account?.Name))
                return false;

            return true;
        }

        return MatchesScope(position);
    }

    private bool ClosedPositionMatchesPnlScope(object closedPosition)
    {
        if (closedPosition == null)
            return false;

        if (UseAccountWidePnlForRevengeGuard)
        {
            if (FilterBySelectedAccount)
            {
                var closedAccountName = GetNestedNameByReflection(closedPosition, "Account");
                if (!IsAccountInScope(closedAccountName))
                    return false;
            }

            return true;
        }

        return ClosedPositionMatchesScope(closedPosition);
    }

    private static bool TryGetClosedPositionTime(object closedPosition, out DateTime closeTime)
    {
        closeTime = default;
        if (closedPosition == null)
            return false;

        var type = closedPosition.GetType();
        var prop = type.GetProperty("CloseTime")
                  ?? type.GetProperty("Time")
                  ?? type.GetProperty("CloseDate")
                  ?? type.GetProperty("ExecutionTime");
        if (prop == null)
            return false;

        if (prop.GetValue(closedPosition) is DateTime dt)
        {
            closeTime = dt;
            return true;
        }

        return false;
    }

    private static string GetNestedNameByReflection(object source, string nestedPropertyName)
    {
        if (source == null)
            return string.Empty;

        var nested = source.GetType().GetProperty(nestedPropertyName)?.GetValue(source);
        if (nested == null)
            return string.Empty;

        var name = nested.GetType().GetProperty("Name")?.GetValue(nested) as string;
        return name ?? string.Empty;
    }

    private static double GetPnlValueByReflection(object source, string pnlPropertyName)
    {
        if (source == null)
            return 0;

        var pnl = source.GetType().GetProperty(pnlPropertyName)?.GetValue(source);
        if (pnl == null)
            return 0;

        var pnlType = pnl.GetType();
        var valueProp = pnlType.GetProperty("Value") ?? pnlType.GetProperty("Amount");
        if (valueProp == null)
            return 0;

        var value = valueProp.GetValue(pnl);
        return value is double d ? d : 0;
    }

    private static double GetClosedPositionPnlValue(object closedPosition)
    {
        if (closedPosition == null)
            return 0;

        // Try common closed-position pnl property names across connections.
        string[] candidateProps = { "NetPnL", "GrossPnL", "PnL", "ProfitLoss", "Profit", "Result" };
        foreach (var propName in candidateProps)
        {
            if (TryGetPnlCandidateValue(closedPosition, propName, out var value))
                return value;
        }

        return 0;
    }

    private static bool TryGetPnlCandidateValue(object source, string pnlPropertyName, out double value)
    {
        value = 0;
        if (source == null)
            return false;

        var prop = source.GetType().GetProperty(pnlPropertyName);
        if (prop == null)
            return false;

        var raw = prop.GetValue(source);
        if (raw == null)
            return false;

        return TryConvertPnlRawValue(raw, out value);
    }

    private static bool TryConvertPnlRawValue(object raw, out double value)
    {
        value = 0;
        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
        }

        string[] nestedCandidates = { "Value", "Amount", "Net", "Gross", "Result" };
        var rawType = raw.GetType();
        foreach (var nestedProp in nestedCandidates)
        {
            var nested = rawType.GetProperty(nestedProp)?.GetValue(raw);
            if (nested == null)
                continue;

            if (TryConvertPnlRawValue(nested, out value))
                return true;
        }

        return false;
    }

    private void RefreshAccountScope()
    {
        scopedAccountNames = ParseAccountCsv(AccountNamesCsv);

        if (Account != null && !string.IsNullOrWhiteSpace(Account.Name))
            scopedAccountNames.Add(Account.Name);

        if (!FilterBySelectedAccount)
            return;

        if (scopedAccountNames.Count > 0)
            Log($"Account scope active for: {string.Join(", ", scopedAccountNames)}", StrategyLoggingLevel.Info);
    }

    private bool HasAccountScope() => scopedAccountNames.Count > 0;

    private bool IsAccountInScope(string accountName)
    {
        if (!FilterBySelectedAccount)
            return true;

        if (string.IsNullOrWhiteSpace(accountName))
            return false;

        return scopedAccountNames.Contains(accountName);
    }

    private static HashSet<string> ParseAccountCsv(string accountNamesCsv)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(accountNamesCsv))
            return result;

        foreach (var item in accountNamesCsv.Split(','))
        {
            var trimmed = item.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private readonly struct EmaSnapshot
    {
        public static EmaSnapshot Invalid => new(0, 0, 0, 0, 0, false);

        public EmaSnapshot(double ema9, double ema21, double referenceClose, double referenceHigh, double referenceLow, bool isValid)
        {
            Ema9 = ema9;
            Ema21 = ema21;
            ReferenceClose = referenceClose;
            ReferenceHigh = referenceHigh;
            ReferenceLow = referenceLow;
            IsValid = isValid;
        }

        public double Ema9 { get; }
        public double Ema21 { get; }
        public double ReferenceClose { get; }
        public double ReferenceHigh { get; }
        public double ReferenceLow { get; }
        public bool IsValid { get; }
    }

    private enum AlertBannerType
    {
        None = 0,
        Guardian = 1,
        Revenge = 2,
        HardStop = 3
    }

    private enum PositionDirection
    {
        Unknown = 0,
        Long = 1,
        Short = 2
    }

    private readonly struct AlertBannerState
    {
        public static AlertBannerState None => new(AlertBannerType.None, string.Empty, DateTime.MinValue);

        public AlertBannerState(AlertBannerType type, string details, DateTime expiresAtLocal)
        {
            Type = type;
            Details = details ?? string.Empty;
            ExpiresAtLocal = expiresAtLocal;
        }

        public AlertBannerType Type { get; }
        public string Details { get; }
        public DateTime ExpiresAtLocal { get; }

        public string Title => Type switch
        {
            AlertBannerType.Guardian => "TRADE GUARDIAN",
            AlertBannerType.Revenge => "REVENGE MODE",
            AlertBannerType.HardStop => "DAILY HARD STOP",
            _ => "STATUS"
        };
    }
}
