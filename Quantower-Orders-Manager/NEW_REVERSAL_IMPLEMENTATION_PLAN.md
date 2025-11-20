# Single-Order Reversal Implementation Plan

## DESIGN CONFIRMED
- Order Type: **MARKET** (Option 1.A)
- Partial Fill: **Leave flat, wait for new signal** (Option 2.A)
- Old SL/TP Cancel: **When first fill detected** (Option 3.B)
- New SL/TP Place: **When reversal order fully filled** (Option 4.A)

---

## CURRENT REVERSAL SYSTEM (TO BE REMOVED)

### Components to Delete:
1. **ReversalStage enum** (lines 72-80)
2. **ReversalPending class** (lines 103-114)
3. **_reversalPending field** (line 115)
4. **_reversalLock** (line 117)
5. **_reversalBypassArmed** (line 100)
6. **_lastLoggedReversalStage** (line 116)
7. **ReversalTickDelay, ReversalMinDelay constants** (lines 118-119)
8. **PositionRemoved reversal logic** (lines 191-294)
9. **ExecuteReversal() method** (lines 2028-2285)
10. **ExecuteDelayedReversalEntry() method** (lines 2287-2368)
11. **LogReversalStage() method** (lines 1811-1823)
12. **Reversal timeout logic** (lines 1579-1600)
13. **Reversal tick detection** (lines 1337-1372)
14. **Reversal pending execution** (lines 1276-1335)
15. **VerifyCompletelyFlat() method** (lines 301-384) - Keep but remove reversal references
16. **PreTradeRiskCheck reversal bypass** (lines 386-456) - Keep but simplify

### Keep (Still Needed):
- Reversal detection in DetermineTradeAction()
- Basic position tracking
- CancelAllUnfilledOrders()
- RefreshExposureTracking()
- Account/Symbol matching helpers

---

## NEW REVERSAL SYSTEM (TO BE IMPLEMENTED)

### Component 1: Reversal Order State Tracker

```csharp
/// <summary>
/// Tracks a single-order reversal in progress
/// </summary>
private class ReversalOrderTracker
{
    public string OrderId { get; set; }              // Reversal order ID
    public Side TargetSide { get; set; }             // Side we're reversing TO
    public Side OriginalSide { get; set; }           // Side we're reversing FROM
    public double OrderQuantity { get; set; }        // Total order qty (|current| + 1)
    public double FlattenQuantity { get; set; }      // Qty needed to close old position
    public double CumulativeFilled { get; set; }     // Fills so far
    public SlTpData MarketData { get; set; }         // Market data for SL/TP calc
    public DateTime InitiatedTime { get; set; }      // When reversal started
    public bool OldSlTpCancelled { get; set; }       // Flag: old protective orders cancelled
    public bool NewSlTpPlaced { get; set; }          // Flag: new protective orders placed
    public List<string> OldItemIds { get; set; }     // Item IDs from old position
    
    public bool FlattenPortionFilled => CumulativeFilled >= FlattenQuantity;
    public bool FullyFilled => Math.Abs(CumulativeFilled - OrderQuantity) < 0.001;
}

private ReversalOrderTracker _activeReversal = null;
```

### Component 2: Get Current Net Quantity

```csharp
/// <summary>
/// Get accurate net quantity from broker positions
/// Returns: (netQty, side, positionIds)
/// </summary>
private (double netQty, Side? side, List<string> positionIds) GetCurrentNetPosition()
{
    var brokerPositions = Core.Instance.Positions
        .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
        .GroupBy(p => p.Id)  // De-duplicate
        .Select(g => g.First())
        .ToList();
    
    if (!brokerPositions.Any())
        return (0, null, new List<string>());
    
    double netQty = brokerPositions.Sum(p => p.Side == Side.Buy ? p.Quantity : -p.Quantity);
    Side? netSide = netQty > 0 ? Side.Buy : netQty < 0 ? Side.Sell : (Side?)null;
    var posIds = brokerPositions.Select(p => p.Id).ToList();
    
    return (Math.Abs(netQty), netSide, posIds);
}
```

### Component 3: Main Reversal Method

```csharp
/// <summary>
/// Execute single-order reversal: place one market order that both closes 
/// old position and opens new position in opposite direction
/// </summary>
private bool ExecuteSingleOrderReversal(SlTpData marketData, TradeSignal entrySignal, TradeSignal exitSignal)
{
    // Step 1: Get current broker position
    var (netQty, currentSide, oldPositionIds) = GetCurrentNetPosition();
    
    if (!currentSide.HasValue || netQty < this.Symbol.MinLot)
    {
        AppLog.Trading("RowanStrategy", "Reversal", 
            $"No position to reverse (netQty={netQty:F2}) - executing normal entry");
        
        // No position to reverse - just do normal entry
        Side targetSide = entrySignal == TradeSignal.OpenBuy ? Side.Buy : Side.Sell;
        this.ComputeTradeAction(marketData, targetSide);
        return true;
    }
    
    // Step 2: Determine target side and quantities
    Side targetSide = currentSide.Value == Side.Buy ? Side.Sell : Side.Buy;
    double flattenQty = netQty;  // Quantity needed to close old position
    double newQty = this.CalculateContractQuantity();  // New position size (usually 1)
    double totalReversalQty = flattenQty + newQty;
    
    // Round to symbol lot size
    totalReversalQty = this.RoundQuantity(totalReversalQty);
    
    if (totalReversalQty < this.Symbol.MinLot)
    {
        AppLog.Error("RowanStrategy", "Reversal", 
            $"Reversal quantity too small ({totalReversalQty}), aborting");
        return false;
    }
    
    // Step 3: Risk check
    if (!PreTradeRiskCheck("SingleOrderReversal", false))
    {
        AppLog.Error("RowanStrategy", "Reversal", "Risk check blocked reversal order");
        return false;
    }
    
    // Step 4: Get old item IDs for cleanup tracking
    var manager = this._manager as TpSlPositionManager;
    var oldItems = manager?.Items
        .OfType<TpSlItemPosition>()
        .Where(item => item.Status != PositionManagerStatus.Closed)
        .Select(item => item.Id)
        .ToList() ?? new List<string>();
    
    // Step 5: Prepare market data for SL/TP calculation
    try
    {
        var history = this.HistoryProvider?.HistoricalData;
        if (history != null && history.Count > 1)
        {
            double rawPivot = targetSide == Side.Buy
                ? history[1][PriceType.Low]
                : history[1][PriceType.High];
            marketData.SlTriggerPrice = rawPivot;
        }
    }
    catch
    {
        marketData.SlTriggerPrice = targetSide == Side.Buy ? _cachedPreviousLow : _cachedPreviousHigh;
    }
    
    // Step 6: Place single market order for reversal
    var comment = GenerateComment();
    this.RegistredGuid.Add(comment);
    
    var marketOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
        .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Market);
    
    if (marketOrderType == null)
    {
        AppLog.Error("RowanStrategy", "Reversal", "No market order type available");
        return false;
    }
    
    var reversalRequest = new PlaceOrderRequestParameters
    {
        Account = this.Account,
        Symbol = this.Symbol,
        Side = targetSide,
        Quantity = totalReversalQty,
        OrderTypeId = marketOrderType.Id,
        Comment = $"{comment}.Reversal"  // Special reversal comment
    };
    
    AppLog.Trading("RowanStrategy", "Reversal", 
        $"Placing SINGLE reversal order: {targetSide} {totalReversalQty} contracts " +
        $"(flatten {flattenQty} + open {newQty}) from {currentSide.Value} {netQty}");
    
    var result = PlaceOrderWithRetry(reversalRequest, "SingleOrderReversal");
    
    if (result.Status != TradingOperationResultStatus.Success)
    {
        AppLog.Error("RowanStrategy", "Reversal", 
            $"Failed to place reversal order: {result.Message}");
        this.RegistredGuid.Remove(comment);
        return false;
    }
    
    // Step 7: Create tracker for this reversal
    _activeReversal = new ReversalOrderTracker
    {
        OrderId = result.OrderId,
        TargetSide = targetSide,
        OriginalSide = currentSide.Value,
        OrderQuantity = totalReversalQty,
        FlattenQuantity = flattenQty,
        CumulativeFilled = 0,
        MarketData = marketData,
        InitiatedTime = DateTime.UtcNow,
        OldSlTpCancelled = false,
        NewSlTpPlaced = false,
        OldItemIds = oldItems
    };
    
    AppLog.Trading("RowanStrategy", "Reversal", 
        $"✅ Reversal order placed: {result.OrderId}, waiting for fills...");
    
    // Log entry details
    foreach (var logsitem in this._logsEntry)
        AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
    foreach (var logsExitem in this._logsExits)
        AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
    
    return true;
}
```

### Component 4: Fill Monitoring (in TradeAdded handler)

```csharp
/// <summary>
/// Monitor reversal order fills and manage SL/TP lifecycle
/// Call this from TradeAdded event or add as new event subscription
/// </summary>
private void MonitorReversalFills(Trade trade)
{
    if (_activeReversal == null)
        return;
    
    // Check if this trade belongs to our reversal order
    if (trade.OrderId != _activeReversal.OrderId)
        return;
    
    // Accumulate filled quantity
    _activeReversal.CumulativeFilled += trade.Quantity;
    
    AppLog.Trading("RowanStrategy", "ReversalFill", 
        $"Reversal fill: {trade.Quantity} @ {trade.Price:F2}, " +
        $"cumulative {_activeReversal.CumulativeFilled}/{_activeReversal.OrderQuantity}");
    
    // Step 1: Old position flattened? Cancel old SL/TP
    if (_activeReversal.FlattenPortionFilled && !_activeReversal.OldSlTpCancelled)
    {
        AppLog.Trading("RowanStrategy", "ReversalFlatten", 
            $"✅ Old position flattened ({_activeReversal.FlattenQuantity} filled), " +
            $"cancelling old SL/TP orders");
        
        CancelOldProtectiveOrders(_activeReversal.OldItemIds);
        _activeReversal.OldSlTpCancelled = true;
        
        // Refresh exposure
        RefreshExposureTracking();
    }
    
    // Step 2: Reversal fully filled? Place new SL/TP
    if (_activeReversal.FullyFilled && !_activeReversal.NewSlTpPlaced)
    {
        AppLog.Trading("RowanStrategy", "ReversalComplete", 
            $"✅ Reversal order fully filled ({_activeReversal.OrderQuantity}), " +
            $"establishing new {_activeReversal.TargetSide} position");
        
        // Small delay to let broker finalize position
        System.Threading.Thread.Sleep(200);
        
        // Refresh to get new position
        RefreshExposureTracking();
        
        // Find the new position that was created
        var newPosition = Core.Instance.Positions
            .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
            .FirstOrDefault(p => p.Side == _activeReversal.TargetSide);
        
        if (newPosition != null)
        {
            AppLog.Trading("RowanStrategy", "ReversalComplete", 
                $"Found new position: {newPosition.Id}, {newPosition.Side} {newPosition.Quantity}");
            
            // Place new SL/TP using existing Trade mechanism
            // This will create a new manager item with proper SL/TP
            PlaceReversalProtectiveOrders(newPosition, _activeReversal.MarketData);
            _activeReversal.NewSlTpPlaced = true;
        }
        else
        {
            AppLog.Error("RowanStrategy", "ReversalComplete", 
                "Reversal filled but no new position found - checking if flat");
            
            // Might be partially filled (only flatten portion)
            var (netQty, _, _) = GetCurrentNetPosition();
            if (netQty < this.Symbol.MinLot)
            {
                AppLog.Trading("RowanStrategy", "ReversalPartial", 
                    "⚠️ Partial fill - position flattened but new side not opened " +
                    "(SAFE: will wait for new signal)");
            }
        }
        
        // Clear tracker
        _activeReversal = null;
        
        AppLog.Trading("RowanStrategy", "ReversalComplete", 
            $"Reversal sequence complete, tracker cleared");
    }
}
```

### Component 5: Cancel Old Protective Orders

```csharp
/// <summary>
/// Cancel SL/TP orders from old position after flatten portion fills
/// </summary>
private void CancelOldProtectiveOrders(List<string> oldItemIds)
{
    if (oldItemIds == null || !oldItemIds.Any())
        return;
    
    var manager = this._manager as TpSlPositionManager;
    if (manager == null)
        return;
    
    foreach (var itemId in oldItemIds)
    {
        var item = manager.Items
            .OfType<TpSlItemPosition>()
            .FirstOrDefault(i => i.Id == itemId);
        
        if (item == null)
            continue;
        
        // Cancel SL order
        var slOrder = item.GetStopLossOrder(this.Symbol);
        if (slOrder != null && 
            (slOrder.Status == OrderStatus.Opened || slOrder.Status == OrderStatus.PartiallyFilled))
        {
            try
            {
                slOrder.Cancel();
                AppLog.Trading("RowanStrategy", "ReversalCleanup", 
                    $"Cancelled old SL order {slOrder.Id} @ {slOrder.TriggerPrice:F2}");
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "ReversalCleanup", 
                    $"Failed to cancel old SL {slOrder.Id}: {ex.Message}");
            }
        }
        
        // Cancel TP order
        var tpOrder = item.GetTakeProfitOrder(this.Symbol);
        if (tpOrder != null && 
            (tpOrder.Status == OrderStatus.Opened || tpOrder.Status == OrderStatus.PartiallyFilled))
        {
            try
            {
                tpOrder.Cancel();
                AppLog.Trading("RowanStrategy", "ReversalCleanup", 
                    $"Cancelled old TP order {tpOrder.Id} @ {tpOrder.Price:F2}");
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "ReversalCleanup", 
                    $"Failed to cancel old TP {tpOrder.Id}: {ex.Message}");
            }
        }
        
        // Unbind from position (will be cleaned up by PositionRemoved event)
        item.SetPosition(null);
    }
}
```

### Component 6: Place New Protective Orders

```csharp
/// <summary>
/// Place SL/TP for new position after reversal completes
/// </summary>
private void PlaceReversalProtectiveOrders(Position newPosition, SlTpData marketData)
{
    if (newPosition == null || this.Strategy == null)
        return;
    
    try
    {
        double entryPrice = newPosition.OpenPrice;
        Side side = newPosition.Side;
        
        // Calculate SL price
        var slPrices = this.Strategy.CalculateSl(marketData, side, entryPrice);
        if (slPrices == null || !slPrices.Any())
        {
            AppLog.Error("RowanStrategy", "ReversalProtective", 
                "Failed to calculate SL for reversal position");
            return;
        }
        
        // Calculate TP price
        var tpPrices = this.Strategy.CalculateTp(marketData, side, entryPrice);
        if (tpPrices == null || !tpPrices.Any())
        {
            AppLog.Error("RowanStrategy", "ReversalProtective", 
                "Failed to calculate TP for reversal position");
            return;
        }
        
        double slPrice = this.Symbol.RoundPriceToTickSize(slPrices[0]);
        double tpPrice = this.Symbol.RoundPriceToTickSize(tpPrices[0]);
        
        // Get order types
        var stopOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
            .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Stop);
        var limitOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
            .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Limit);
        
        if (stopOrderType == null || limitOrderType == null)
        {
            AppLog.Error("RowanStrategy", "ReversalProtective", 
                "Order types not available for SL/TP");
            return;
        }
        
        // Place SL order
        var slRequest = new PlaceOrderRequestParameters
        {
            Account = this.Account,
            Symbol = this.Symbol,
            Side = side == Side.Buy ? Side.Sell : Side.Buy,
            Quantity = newPosition.Quantity,
            OrderTypeId = stopOrderType.Id,
            TriggerPrice = slPrice,
            TimeInForce = TimeInForce.Day,
            PositionId = newPosition.Id,
            AdditionalParameters = new List<SettingItem>
            {
                new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
            }
        };
        
        var slResult = PlaceOrderWithRetry(slRequest, "ReversalSL");
        if (slResult.Status == TradingOperationResultStatus.Success)
        {
            AppLog.Trading("RowanStrategy", "ReversalProtective", 
                $"✅ SL placed @ {slPrice:F2} for new position {newPosition.Id}");
        }
        
        // Place TP order
        var tpRequest = new PlaceOrderRequestParameters
        {
            Account = this.Account,
            Symbol = this.Symbol,
            Side = side == Side.Buy ? Side.Sell : Side.Buy,
            Quantity = newPosition.Quantity,
            OrderTypeId = limitOrderType.Id,
            Price = tpPrice,
            TimeInForce = TimeInForce.Day,
            PositionId = newPosition.Id,
            AdditionalParameters = new List<SettingItem>
            {
                new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
            }
        };
        
        var tpResult = PlaceOrderWithRetry(tpRequest, "ReversalTP");
        if (tpResult.Status == TradingOperationResultStatus.Success)
        {
            AppLog.Trading("RowanStrategy", "ReversalProtective", 
                $"✅ TP placed @ {tpPrice:F2} for new position {newPosition.Id}");
        }
    }
    catch (Exception ex)
    {
        AppLog.Error("RowanStrategy", "ReversalProtective", 
            $"Error placing protective orders: {ex.Message}");
    }
}
```

### Component 7: TradeAdded Event Subscription

```csharp
// Add to Init() method or RegisterHandlers():
Core.Instance.TradeAdded += OnTradeAdded_MonitorReversals;

// Add to Dispose():
Core.Instance.TradeAdded -= OnTradeAdded_MonitorReversals;

// Handler:
private void OnTradeAdded_MonitorReversals(Trade trade)
{
    if (trade == null)
        return;
    
    // Only process our trades
    if (!AccountMatches(trade.Account) || !SymbolsMatch(trade.Symbol))
        return;
    
    MonitorReversalFills(trade);
}
```

---

## INTEGRATION PLAN

### Replace Calls:
1. **Line 1674-1678**: Change from old ExecuteReversal to new
2. **Line 1357-1371**: Tick-level reversal detection

### Remove:
- Lines 72-80: ReversalStage enum
- Lines 100-119: ReversalPending class and related fields
- Lines 191-294: PositionRemoved reversal handler
- Lines 1276-1335: Reversal pending execution
- Lines 1579-1600: Reversal timeout
- Lines 1811-1823: LogReversalStage
- Lines 2028-2368: Old reversal methods

### Simplify:
- VerifyCompletelyFlat: Remove reversal-specific logic
- PreTradeRiskCheck: Remove reversal bypass token

---

## SAFETY CHECKS

1. ✅ **Partial fill safety**: If only flatten portion fills, we're flat (safe state)
2. ✅ **Order rejection**: Original position intact if order rejected
3. ✅ **Duplicate prevention**: Using existing exposure tracking
4. ✅ **Session guard**: PreTradeRiskCheck still applies
5. ✅ **Max loss guard**: PreTradeRiskCheck still applies
6. ✅ **Old SL/TP cleanup**: Cancelled reactively after flatten
7. ✅ **New SL/TP placement**: Only after full fill confirmed
8. ✅ **Logging**: Comprehensive at each step

---

## TESTING CHECKLIST

After implementation:
- [ ] Reversal Long→Short works (2 contracts short order)
- [ ] Reversal Short→Long works (2 contracts long order)
- [ ] Partial fill leaves flat (no new position)
- [ ] Full fill creates new position with SL/TP
- [ ] Old SL/TP cancelled after flatten
- [ ] No orphaned orders on chart
- [ ] Exposure tracking remains accurate
- [ ] Max open limit still respected
- [ ] Session guard still works
- [ ] Logs show clear reversal sequence

---

**Ready to proceed with implementation?** I'll work methodically to:
1. Add new reversal components
2. Remove all old reversal code
3. Update integration points
4. Test for compilation
5. Verify all logic paths

Shall I begin? 🚀

