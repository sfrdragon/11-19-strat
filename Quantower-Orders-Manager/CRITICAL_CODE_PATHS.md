# Critical Code Paths - Quick Reference

## Path 1: Entry → SL/TP Capture

### When You Place an Entry Order:
```
1. RowanStrategy.ComputeTradeAction() → ConditionableBase.Trade()
2. ConditionableBase.Trade() → Calculates SL/TP prices → TpSlPositionManager.PlaceEntryOrder()
3. TpSlPositionManager.PlaceEntryOrder():
   - Creates SlTpHolder with tick offsets
   - Calls Core.Instance.PlaceOrder(orderobj)
   - Records targetItem.ExpectedSlPrice = sl[0].TriggerPrice
   - Records targetItem.ExpectedTpPrice = tp[0].Price
   - Schedules Task.Delay(300) → DiscoverOrdersForItem(targetItem)
4. DiscoverOrdersForItem() [300ms later]:
   - Scans Core.Instance.Orders where OrderTypeBehavior.Stop AND Math.Abs(TriggerPrice - ExpectedSlPrice) < 2 ticks
   - Sets item.StopLossOrderId = foundOrder.Id
   - Logs "✅ Discovered SL order XXX at price YYY"
```

**Critical:** If you see **"⚠️ SL order not found near expected price"**, the broker didn't create the OCO bracket.

---

## Path 2: Position Binding (Prevents Duplicates)

### When Rithmic Fires PositionAdded:
```
1. TpSlPositionManager.Instance_PositionAdded() → CatchPosition(pos)
2. CatchPosition():
   - Query: alreadyTracked = Items where PositionId == pos.Id
   - IF alreadyTracked.Any():
       → Update all tracked items with new pos reference
       → RETURN IMMEDIATELY (prevents duplicate binding)
   - ELSE:
       → Find candidate = Items where Side matches AND PositionId is empty AND Position is null
       → Bind candidate.SetPosition(pos)
```

**Critical:** Early return at step 2 prevents same `Position.Id` from binding to multiple items.

---

## Path 3: Trailing Stop Update (Every Bar)

### When Bar Closes:
```
1. RowanStrategy.Update() → Prepares market data (currentBarPrice, previousLow/High, atrInTicks)
2. Trailing loop:
   FOR EACH activeItem in manager.Items:
       - Call slStrategy.UpdateSl(trailingData, activeItem) → Returns Func<double, double>
       - Call slManager.UpdateSl(activeItem, updateFunction)
3. TpSlPositionManager.UpdateSl():
   - Call item.GetStopLossOrder(_symbol)
   - IF slOrder found:
       → newTriggerPrice = updateFunction(slOrder.TriggerPrice)
       → Snap to tick size
       → IF price changed: Core.Instance.ModifyOrder(slOrder, triggerPrice: newTriggerPrice)
       → Update item.ExpectedSlPrice = newTriggerPrice
4. Validation (same loop):
   - Call item.GetStopLossOrder(Symbol) again
   - IF null AND position active → Emergency close
   - IF price drifts from expected → Sync ExpectedSlPrice
```

**Critical:** You should see `[TpSlPositionManager][UpdateSl] Updating SL: X → Y` when trailing occurs.

---

## Path 4: Exposure Tracking (Every Bar)

### RefreshExposureTracking Flow:
```
1. RowanStrategy.Update() → RefreshExposureTracking() → EnsureExposureConsistency(false)
2. EnsureExposureConsistency():
   - Query brokerPositions = Core.Instance.Positions where SymbolsMatch AND AccountMatches
   - De-duplicate: uniquePositions = brokerPositions.GroupBy(p => p.Id).Select(g => g.First())
   - Calculate contractCount = (int)Round(Sum(Quantity) / MinLot)
   - Reconcile:
       → orphanedItems = manager.Items where PositionId not in broker → Call SetPosition(null)
       → duplicateItems = manager.Items.GroupBy(PositionId) where Count > 1 → Keep newest, remove rest
   - Set ExposedCount = contractCount (BROKER COUNT, not item count)
```

**Critical:** Log shows `"Broker: X contracts, Manager: Y items, Exposed: Z"`. X and Z should match.

---

## Path 5: Reversal Safety

### When Reversal Triggered:
```
1. RowanStrategy.Update() → Entry=OpenSell, Exit=Wait, ExposeSide=Long → Action=Revert
2. Reversal initiation:
   - _reversalPending = new ReversalPending { ClosingItemIds = activeItems.Select(i => i.Id).ToHashSet() }
   - FOR EACH activeItem: item.Quit()
3. PositionRemoved event handler:
   - Remove item.Id from _reversalPending.ClosingItemIds
   - IF ClosingItemIds.Count == 0:
       → Call VerifyCompletelyFlat(3)
4. VerifyCompletelyFlat():
   - Retry loop (3 attempts × 200ms):
       → IF Core.Instance.Positions.Any(our symbol/account) → Continue (wait)
       → IF Core.Instance.Orders.Any(unfilled) → Cancel all → Continue
       → Call manager.PruneOrphanedItems() + RefreshExposureTracking()
       → IF ExposedCount == 0 → Return true
5. IF flat → ComputeTradeAction(oppositeSide)
```

**Critical:** Logs should show `"✅ Completely flat"` before opposite entry. If timeout, check for uncancelled orders.

---

## Path 6: Max Open Position Guard

### Before Every Trade:
```
1. RowanStrategy.Update() → Action = Buy/Sell → PreTradeRiskCheck("Entry")
2. PreTradeRiskCheck():
   - Check max session loss
   - Call RefreshExposureTracking() → Updates ExposedCount from broker
   - Query brokerCount = Core.Instance.Positions.GroupBy(Id).Count()
   - IF ExposedCount != brokerCount:
       → Log warning
       → ExposedCount = brokerCount (use broker as truth)
   - IF ExposedCount >= _maxOpen:
       → Log "Max open positions reached (X/Y)"
       → Return false (blocks trade)
```

**Critical:** Every blocked entry should log actual count: `"(2/2)"` means 2 positions open, max is 2.

---

## Path 7: Exit Cleanup

### When TP/SL Hit or Manual Close:
```
1. Broker fills SL/TP → PositionRemoved event
2. TpSlPositionManager.PositionRemoved handler:
   - Find items where Position.Id == removed.Id
   - Call item.TryUpdateStatus() → Detects Position == null → Status = Closed
   - Call item.Quit()
3. item.Quit():
   - Close activePos (if exists)
   - Find slOrder via GetStopLossOrder() [3-tier fallback]
   - IF found AND status Opened → Cancel
   - Same for TP
   - GroupId scan fallback (catches any remaining OCO orders)
```

**Critical:** You should see `"Cancelled SL order XXX"` and `"Cancelled TP order YYY"` after every exit.

---

## Debugging Quick Checks

### Issue: "SL not trailing"
**Check:**
1. Is `[TpSlPositionManager][Discovery] ✅ Discovered SL order` logged after entry?
2. Is `[TpSlPositionManager][UpdateSl] Updating SL:` logged on subsequent bars?
3. Does `item.ExpectedSlPrice` have a valid value (not NaN)?

**If No:**
- Discovery may have failed → Check if SL order exists in `Core.Instance.Orders`
- Price proximity may be too tight → Increase tolerance from 2 ticks to 5 ticks
- `GetStopLossOrder()` may be returning null → Add diagnostic logging

---

### Issue: "Max open positions breached"
**Check:**
1. Does `[RowanStrategy][ExposureReconcile] Broker: X contracts` match your UI?
2. Is `"Found N items for position ID"` appearing (duplicate cleanup)?
3. Does `[RowanStrategy][RiskCheck] Exposure mismatch` log show correction?

**If No:**
- `CatchPosition` early return may not be executing → Add log at line 601
- Broker position query may be missing positions → Check `SymbolsMatch` logic
- Periodic reconciliation may not be running → Verify `_exposureReconcileInterval` > 0

---

### Issue: "Reversal timeout"
**Check:**
1. Does `[RowanStrategy][FlatnessCheck] Cancelling X unfilled orders` appear?
2. Are those orders actually cancelled (check `Order history` logs)?
3. Does broker still show positions after `ForceClosePositions`?

**If Timeout:**
- `VerifyCompletelyFlat` may need more retries (increase from 3 to 5)
- Sleep duration may be too short (increase from 200ms to 500ms)
- Broker may reject close requests → Check for `"Failed to close position"` errors

---

### Issue: "SL/TP orphaned on chart"
**Check:**
1. After exit, do you see `"Cancelled SL order"` AND `"Cancelled TP order"`?
2. Is `item.ExpectedSlPrice` set to a valid value?
3. Does `GetStopLossOrder()` find the order (check Priority 2/3 logs)?

**If Orphaned:**
- Fuzzy match tolerance may be too tight → Price may have drifted > 1 point
- Order may have been modified externally → Check `[SlValidation] SL price drift`
- `Quit()` may be called before discovery completes → Increase discovery delay to 500ms

---

## Emergency Recovery Commands

If strategy gets into inconsistent state during live trading:

1. **Stop Strategy** → `OnStop()` calls `Dispose()` → Cancels all tracked items
2. **Manually Cancel All Orders** → Quantower UI → Order panel → Cancel all for your account
3. **Verify Flat:** Check `Core.Instance.Positions` in Quantower's Position panel
4. **Restart Strategy** → Fresh `_itemsDictionary`, `ExposedCount = 0`

---

## Success Metrics (Expected in Logs)

After implementing this system, you should see:

- ✅ `"✅ Discovered SL order"` → **100%** of entries
- ✅ `"Updating SL: X → Y"` → **Every bar** price moves favorably
- ✅ `"Broker: X contracts, Manager: X items"` → **Always equal**
- ✅ `"✅ Completely flat"` → **Before every reversal**
- ✅ `"Cancelled SL order"` + `"Cancelled TP order"` → **Every exit**
- ✅ `"Max open positions reached (2/2)"` → **Blocks 3rd entry** when max=2

If any of these are **not** appearing, refer to the debugging section above.

---

**Build Version:** Release (October 30, 2025)  
**Target Platform:** Quantower v1.144.12  
**Broker Compatibility:** Rithmic (tested), dxFeed (compatible)  
**Strategy Status:** Production-Ready

