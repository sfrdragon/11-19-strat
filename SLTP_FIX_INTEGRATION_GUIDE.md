# SL/TP Fix Integration Guide

## Overview
This document outlines the comprehensive fixes for Stop Loss and Take Profit management issues in the Quantower futures trading strategy.

## New Components Created

### 1. ProtectiveOrderManager.cs
**Purpose**: Robust SL/TP placement with retry logic and validation

**Features**:
- Automatic retry on placement failure (3 attempts with exponential backoff)
- Price validation after placement
- Atomic bracket placement (both SL and TP)
- Orphaned order cleanup
- Position validation

**Key Methods**:
```csharp
PlaceStopLoss(Position position, double slPrice, string comment)
PlaceTakeProfit(Position position, double tpPrice, string comment)
PlaceBracket(Position position, double slPrice, double tpPrice, string baseComment)
CancelProtectiveOrders(string positionId, string reason)
CleanupOrphanedOrders(string strategyPrefix)
ValidateProtection(Position position)
```

### 2. AtomicReversalManager.cs
**Purpose**: Guaranteed atomic position reversals

**Reversal Flow**:
1. Capture current position state
2. Cancel old SL/TP orders FIRST
3. Place reversal market order
4. Wait for new position to appear
5. Verify old position fully closed
6. Calculate new SL/TP from actual fill price
7. Place new protective orders with validation
8. Final validation or emergency flatten

**Features**:
- Atomic completion or rollback
- Emergency flattening if protection fails
- Proper cleanup of old orders
- Timeout protection
- Comprehensive logging

### 3. PositionHealthMonitor.cs
**Purpose**: Continuous health monitoring and auto-repair

**Features**:
- Runs on every tick
- Validates all positions have SL/TP
- Auto-places missing protective orders
- Emergency flatten after 10 seconds without protection
- Periodic orphan cleanup (every 2 seconds)
- Tracks repair attempts per position

## Integration Steps for RowanStrategy.cs

### STEP 1: Add New Fields

```csharp
// New robust managers
private ProtectiveOrderManager _protectiveOrderManager;
private AtomicReversalManager _atomicReversalManager;
private PositionHealthMonitor _healthMonitor;

// Health check tracking
private DateTime _lastHealthCheck = DateTime.MinValue;
private const int HEALTH_CHECK_INTERVAL_MS = 500;  // Check every 500ms
```

### STEP 2: Initialize in OnRun()

```csharp
protected override void OnRun()
{
    // ... existing initialization ...

    // Initialize robust managers
    _protectiveOrderManager = new ProtectiveOrderManager(this.Symbol, this.Account);

    _atomicReversalManager = new AtomicReversalManager(
        this.Symbol,
        this.Account,
        _protectiveOrderManager,
        this._manager as TpSlPositionManager,
        this.Strategy);

    _healthMonitor = new PositionHealthMonitor(
        this.Symbol,
        this.Account,
        _protectiveOrderManager,
        this.Strategy);

    AppLog.System("RowanStrategy", "RobustManagers", "Initialized robust SL/TP managers");
}
```

### STEP 3: Add Health Check to ProcessHistoryUpdate()

```csharp
private void ProcessHistoryUpdate(HistoryEventArgs e, HistoryUpdAteType updateType)
{
    // ... existing code ...

    // PHASE 0: HEALTH CHECK - Run every 500ms
    if ((DateTime.UtcNow - _lastHealthCheck).TotalMilliseconds >= HEALTH_CHECK_INTERVAL_MS)
    {
        try
        {
            // Build current market data for health monitor
            var healthMarketData = new SlTpData
            {
                Symbol = this.Symbol,
                currentPrice = currentBarPrice,
                AtrInTicks = atrInTicks,
                PreviousLow = previousLow,
                PreviousHigh = previousHigh
            };

            var healthResult = _healthMonitor.CheckAndRepair(healthMarketData);

            if (!healthResult.AllHealthy)
            {
                AppLog.System("RowanStrategy", "HealthCheck",
                    $"⚠️ {healthResult}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("RowanStrategy", "HealthCheck", $"Health check failed: {ex.Message}");
        }

        _lastHealthCheck = DateTime.UtcNow;
    }

    // ... rest of existing code ...
}
```

### STEP 4: Replace Trade() Method for Entry

Replace the existing Trade() method's SL/TP placement logic:

```csharp
private void Trade(Side side, double price, SlTpData slData, SlTpData tpData)
{
    // ... existing order placement code ...

    if (result.Status == TradingOperationResultStatus.Success)
    {
        AppLog.Trading("RowanStrategy", "EntryPlaced",
            $"Entry order placed: {result.OrderId}");

        // OLD CODE - REMOVE:
        // this._manager?.PlanBracket(comment, plannedSl, plannedTp);

        // NEW CODE - Use robust bracket planning
        var manager = this._manager as TpSlPositionManager;
        if (manager != null)
        {
            // Calculate SL/TP
            var sl = Strategy.CalculateSl(slData, side, price);
            var tp = Strategy.CalculateTp(tpData, side, price);

            double? plannedSl = null;
            if (sl != null && sl.Count > 0)
                plannedSl = this.Symbol.RoundPriceToTickSize(sl[0]);

            double? plannedTp = null;
            if (tp != null && tp.Count > 0)
                plannedTp = this.Symbol.RoundPriceToTickSize(tp[0]);

            // Plan bracket in manager
            manager.PlanBracket(comment, plannedSl, plannedTp);

            AppLog.Trading("RowanStrategy", "BracketPlanned",
                $"Planned bracket for {comment}: SL={plannedSl:F2}, TP={plannedTp:F2}");
        }
    }
}
```

### STEP 5: Replace Reversal Logic

Replace `ExecuteSingleOrderReversal()` with:

```csharp
private bool ExecuteAtomicReversal(SlTpData marketData, TradeSignal entrySignal, TradeSignal exitSignal)
{
    // Determine new side from entry signal
    Side newSide = entrySignal == TradeSignal.OpenBuy ? Side.Buy : Side.Sell;

    // Calculate reversal quantity
    double quantity = CalculateContractQuantity();

    // Generate comment
    var comment = GenerateComment();
    this.RegistredGuid.Add(comment);

    AppLog.Trading("RowanStrategy", "ReversalTrigger",
        $"Executing atomic reversal to {newSide} {quantity} contracts");

    // Use atomic reversal manager
    var reversalResult = _atomicReversalManager.ExecuteReversal(
        newSide,
        quantity,
        marketData,
        comment,
        (commentParam) =>
        {
            // Place reversal order using existing logic
            var marketOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
                .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Market);

            var request = new PlaceOrderRequestParameters
            {
                Account = this.Account,
                Symbol = this.Symbol,
                Side = newSide,
                Quantity = quantity,
                OrderTypeId = marketOrderType.Id,
                Comment = $"{commentParam}.{OrderTypeSubcomment.Entry}",
                TimeInForce = TimeInForce.Day
            };

            return PlaceOrderWithRetry(request, "AtomicReversal");
        });

    if (reversalResult.Success)
    {
        // Force health validation after reversal
        _healthMonitor.ForceValidation();

        // Update manager tracking
        var manager = this._manager as TpSlPositionManager;
        manager?.CreateItem(comment);

        AppLog.Trading("RowanStrategy", "ReversalComplete",
            $"✅ Atomic reversal completed: {newSide} {reversalResult.NewPosition?.Quantity} @ " +
            $"{reversalResult.NewPosition?.OpenPrice:F2}");

        return true;
    }
    else
    {
        AppLog.Error("RowanStrategy", "ReversalFailed",
            $"❌ Atomic reversal failed: {reversalResult.Message}");

        this.RegistredGuid.Remove(comment);
        return false;
    }
}
```

Then replace all calls to `ExecuteSingleOrderReversal()` with `ExecuteAtomicReversal()`.

### STEP 6: Fix Trailing SL Logic

The existing trailing SL logic in `ProcessHistoryUpdate()` around line 2533 needs to ensure proper market data:

```csharp
// PHASE 2: Execute SL Trailing on EVERY TICK for all open positions
if (this._manager is TpSlPositionManager trailingManager && this.Strategy != null && !double.IsNaN(currentBarPrice))
{
    var slStrategy = this.Strategy as RowanSlTpStrategy;
    var slMode = slStrategy?.SlModeType ?? SlMode.PreviousCandle;

    var activeItems = trailingManager.Items
        .OfType<TpSlItemPosition>()
        .Where(it => it.Status != PositionManagerStatus.Closed &&
                     (!string.IsNullOrEmpty(it.StopLossOrderId) || !double.IsNaN(it.ExpectedSlPrice)))
        .ToList();

    foreach (var trailingItem in activeItems)
    {
        try
        {
            var slOrder = trailingItem.GetStopLossOrder(this.Symbol);
            if (slOrder == null)
            {
                // Log missing SL (health monitor will attempt to repair)
                continue;
            }

            double currentSlPrice = slOrder.TriggerPrice;

            if (slMode == SlMode.PreviousCandle && !isBarClose)
            {
                if (!ShouldTrailPreviousCandleIntrabar(trailingItem, currentBarPrice, currentSlPrice, slStrategy))
                    continue;
            }

            // CRITICAL FIX: Ensure correct SlTriggerPrice for trailing calculation
            var trailingMarketData = new SlTpData
            {
                Symbol = this.Symbol,
                currentPrice = currentBarPrice,
                PreviousLow = previousLow,
                PreviousHigh = previousHigh,
                AtrInTicks = atrInTicks,
                SlTriggerPrice = trailingItem.Side == Side.Buy ? previousLow : previousHigh  // CORRECT PIVOT
            };

            // Calculate new SL
            var updateSlFunc = this.Strategy.UpdateSl(trailingMarketData, trailingItem);
            double proposedSlPrice = updateSlFunc(currentSlPrice);

            // Only trail favorably
            bool shouldUpdate = false;
            if (trailingItem.Side == Side.Buy && proposedSlPrice > currentSlPrice)
                shouldUpdate = true;
            else if (trailingItem.Side == Side.Sell && proposedSlPrice < currentSlPrice)
                shouldUpdate = true;

            if (shouldUpdate && Math.Abs(proposedSlPrice - currentSlPrice) > this.Symbol.TickSize * 0.1)
            {
                proposedSlPrice = this.Symbol.RoundPriceToTickSize(proposedSlPrice);

                AppLog.Trading("RowanStrategy", "SlTrailTick",
                    $"Item {trailingItem.Id.Substring(0, 8)}: SL trailing {currentSlPrice:F2} → {proposedSlPrice:F2}");

                // Update via manager
                trailingManager.UpdateSl(trailingItem, proposedSlPrice);
                trailingItem.ExpectedSlPrice = proposedSlPrice;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("RowanStrategy", "SlTrailTick",
                $"Failed to trail SL for item {trailingItem.Id}: {ex.Message}");
        }
    }
}
```

### STEP 7: Cleanup on Stop

```csharp
protected override void OnStop()
{
    // Reset health monitor
    _healthMonitor?.Reset();

    // Cleanup any remaining orphans
    _protectiveOrderManager?.CleanupOrphanedOrders(this.StrategyName);

    // ... existing cleanup code ...
}
```

### STEP 8: Remove Old Reversal Tracking

Remove these old fields that are no longer needed:
```csharp
// OLD - REMOVE:
// private ReversalOrderTracker _activeReversal;
// private PendingReversal _pendingReversal;
```

## Key Improvements

### 1. Atomic Reversals
- Old position fully closed before new SL/TP placed
- New SL/TP calculated from actual fill price
- Guaranteed completion or rollback
- Emergency flatten if protection fails

### 2. Robust SL/TP Placement
- 3 retry attempts with exponential backoff
- Validation after placement
- Proper price rounding
- Automatic error correction

### 3. Continuous Health Monitoring
- Checks every 500ms
- Auto-repairs missing orders
- Emergency flatten after 10 seconds
- Orphan cleanup every 2 seconds

### 4. Correct Trailing Logic
- Proper SlTriggerPrice for each side
- Clear market data preparation
- Monotonic trailing (never loosens)
- Respects SL modes

### 5. Better Error Handling
- Comprehensive logging
- Graceful degradation
- Emergency procedures
- State cleanup

## Testing Checklist

- [ ] New entry places SL/TP correctly
- [ ] SL/TP values are correct (not from old position)
- [ ] Reversal completes fully (old closed, new opened)
- [ ] New SL/TP placed after reversal
- [ ] Old SL/TP cancelled before reversal
- [ ] Exit signal removes all SL/TP
- [ ] Trailing SL updates correctly
- [ ] Health monitor detects missing SL/TP
- [ ] Health monitor auto-repairs
- [ ] Emergency flatten triggers when needed
- [ ] Orphaned orders cleaned up
- [ ] Multiple positions handled correctly
- [ ] Concurrent reversals handled safely

## Commit Message

```
Fix: Comprehensive SL/TP and reversal management overhaul

- Add ProtectiveOrderManager for robust SL/TP placement with retry
- Add AtomicReversalManager for guaranteed reversal completion
- Add PositionHealthMonitor for continuous validation and auto-repair
- Fix trailing SL logic to use correct pivot prices
- Fix reversal flow to cancel old orders before placing new ones
- Add emergency flatten for unprotected positions
- Add aggressive orphaned order cleanup
- Improve error handling and logging throughout

Resolves: SL/TP not placed on entries, incorrect TP values, incomplete reversals,
orphaned orders after exits, trailing SL not updating
```

## Notes

- The new managers are designed to be defensive and fail-safe
- All operations have retry logic and validation
- Emergency procedures protect capital when normal flow fails
- Extensive logging enables debugging
- Health monitor provides continuous oversight
- Changes are backward compatible with existing strategy parameters
