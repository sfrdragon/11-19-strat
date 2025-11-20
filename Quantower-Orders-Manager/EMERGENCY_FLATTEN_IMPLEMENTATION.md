# Emergency Flatten Implementation - Market Order Retry System

## Overview
All emergency and forced closes in RowanStrategy now use **market orders** with a robust 3-attempt retry system. `Position.Close()` is only used as absolute last resort after all market order attempts fail.

---

## Implementation Summary

### New Infrastructure

**1. EmergencyCloseAttempt Tracking Class**
```csharp
private class EmergencyCloseAttempt
{
    public string PositionId { get; set; }
    public int AttemptCount { get; set; }
    public DateTime LastAttemptTime { get; set; }
    public string LastOrderId { get; set; }
    public bool FallbackUsed { get; set; }
}
```

**2. Tracking Dictionary**
```csharp
private readonly Dictionary<string, EmergencyCloseAttempt> _emergencyCloseAttempts;
```

Prevents infinite retry loops by tracking:
- How many attempts made per position
- Whether Position.Close() fallback was already used
- Last order ID for debugging

**3. Cleanup in PositionRemoved Event**
```csharp
Core.Instance.PositionRemoved += (position) =>
{
    // ... existing cleanup ...
    _emergencyCloseAttempts.Remove(position.Id);
};
```

---

## Core Method: EmergencyFlattenPosition

**Location**: RowanStrategy.cs, added after CleanupOrphanedProtectiveOrders

### Retry Logic Flow

```
For each attempt (max 3):
├─ Attempt N/3
├─ Build market order (opposite side, rounded quantity)
├─ Place order via Core.Instance.PlaceOrder
│
├─ If placement FAILS:
│  ├─ Log error
│  ├─ If attempts < 3: Wait 1000ms, retry
│  └─ If attempts == 3: Break to fallback
│
├─ If placement SUCCESS:
│  ├─ Log order ID
│  └─ Poll for 3 seconds (3× 1000ms intervals):
│     ├─ Check if position still exists
│     ├─ If gone: SUCCESS, clear tracker, return true
│     └─ If still exists after 3s: Retry next market attempt
│
└─ After 3 failed attempts:
   └─ Use Position.Close() as absolute last resort
      ├─ Set FallbackUsed = true (prevents future retries)
      ├─ Wait 1000ms, verify position closed
      └─ Return true/false based on result
```

### Parameters
- `Position position`: The broker position to flatten
- `string context`: Logging context (e.g., "ProtectionGuardian_NoSL")

### Returns
- `true`: Position successfully closed (via market order or fallback)
- `false`: All attempts failed, position still exists

### Timing
- **Best case**: 1-3 seconds (first market order fills immediately)
- **Typical case**: 3-6 seconds (market order fills after 1-2 polling intervals)
- **Worst case**: ~10 seconds (all 3 market attempts timeout, fallback used)

---

## Helper Method: EmergencyFlattenAllPositions

**Purpose**: Bulk flatten for ForceClosePositions

```csharp
private bool EmergencyFlattenAllPositions(string context)
{
    var positions = Core.Instance.Positions
        .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
        .ToList();
    
    bool allClosed = true;
    foreach (var pos in positions)
    {
        if (!EmergencyFlattenPosition(pos, context))
            allClosed = false;
    }
    
    return allClosed;
}
```

Returns `true` only if **all** positions successfully closed.

---

## Replacement Locations

### 1. EnforceProtectionInvariants - Untracked Position
**Before**:
```csharp
try {
    position.Close();
} catch (Exception closeEx) {
    AppLog.Error(...);
}
```

**After**:
```csharp
EmergencyFlattenPosition(position, "ProtectionGuardian_Untracked");
```

### 2. EnforceProtectionInvariants - Missing SL
**Before**:
```csharp
try {
    position.Close();
} catch (Exception closeEx) {
    AppLog.Error(...);
}
```

**After**:
```csharp
EmergencyFlattenPosition(position, "ProtectionGuardian_NoSL");
```

### 3. SL Validation Emergency
**Before**:
```csharp
bundleItem.Position?.Close();
```

**After**:
```csharp
if (bundleItem.Position != null)
{
    EmergencyFlattenPosition(bundleItem.Position, "SlValidation");
}
```

### 4. ForceClosePositions Bulk Loop
**Before**:
```csharp
foreach (var pos in brokerPositions)
{
    try {
        pos.Close();
        AppLog.System(...);
    } catch (Exception ex) {
        AppLog.Error(...);
    }
}
```

**After**:
```csharp
bool allFlattened = EmergencyFlattenAllPositions("ForceClose");
if (!allFlattened)
{
    AppLog.Error("RowanStrategy", "ForceClose",
        "Some positions failed to flatten via market orders (will verify in polling loop)");
}
```

---

## Position.Close() Usage After Implementation

**Remaining uses of Position.Close():**

1. **TpSlItemPosition.Quit()** - Manager layer (NOT modified per user requirement)
   - Location: `OperationSystemAdv/DDDCore/TpSlItemPosition.cs:377`
   - Context: Part of native PositionRemoved event handling
   - Status: **ALLOWED**

2. **EmergencyFlattenPosition fallback** - Strategy layer
   - Location: RowanStrategy.cs, inside EmergencyFlattenPosition method
   - Context: Only after 3 market order attempts fail
   - Frequency: Should be **ZERO** in normal operation
   - Status: **DOCUMENTED LAST RESORT**

**All other strategy-level emergency closes now use market orders.**

---

## Safety Guarantees

### Rate Limiting
- Each position can only have emergency close attempted once at a time
- `_emergencyCloseAttempts` dictionary prevents concurrent retries
- `FallbackUsed` flag prevents re-attempting after fallback succeeds/fails

### No Infinite Loops
- Maximum 3 market order attempts per position
- Each attempt has 3-second verification timeout
- After 3 failures, fallback runs once
- If fallback also fails, returns false and logs critical error
- Guardian continues monitoring but won't retry same position (rate limit)

### Verification Polling
- Every market order placement is verified for 3 seconds
- Checks actual broker position state (not cached)
- Only proceeds to next attempt if position truly didn't close

---

## Edge Case Handling

| Scenario | Behavior |
|----------|----------|
| Market order type not available | Skip to fallback immediately |
| Market order placement fails | Wait 1s, retry (up to 3×) |
| Market order places but doesn't fill | Wait 3s polling, then retry |
| All 3 market attempts fail | Use Position.Close() fallback |
| Fallback also fails | Log critical error, return false |
| Position closes mid-retry | Detected on next poll, success returned |
| Guardian runs again before retry completes | Rate limiter prevents duplicate attempts |
| PositionRemoved fires during retry | Tracking cleared, next poll shows success |

---

## Performance Impact

### Normal Operation
- Zero impact (emergency paths never execute)
- EnforceProtectionInvariants runs every tick as before
- No emergency closes should occur

### Emergency Scenarios
- **Per unprotected position**: 3-10 seconds to resolve
- **Thread blocking**: Yes, uses `Thread.Sleep` for verification
- **Acceptable**: Emergency paths only fire on critical failures

### Worst-Case Scenario
- 3 positions need emergency close
- Each takes 10 seconds (3 failed market attempts + fallback)
- Total: ~30 seconds to fully resolve
- Strategy remains active, continues monitoring throughout

---

## Testing Checklist

### Verify Market Orders Used
- [ ] Trigger untracked position → check logs for "Emergency flatten attempt 1/3"
- [ ] Verify market order placed (not Position.Close initially)
- [ ] Verify 3-second polling logged
- [ ] Check final result (success or fallback)

### Verify Rate Limiting Works
- [ ] Create scenario where guardian sees same unprotected position on consecutive ticks
- [ ] Verify only ONE set of retry attempts (not multiple concurrent)
- [ ] Check `_emergencyCloseAttempts` prevents duplicates

### Verify Fallback Logic
- [ ] Artificially prevent market orders (remove allowed order type in sim)
- [ ] Verify immediate fallback to Position.Close()
- [ ] Verify `FallbackUsed` flag prevents further retries

### Code Verification
- [ ] Grep for `Position.Close()` in RowanStrategy.cs
- [ ] Should only find:
  - EmergencyFlattenPosition fallback block (documented)
  - No other strategy-level uses
- [ ] Grep for `pos.Close()` - should be zero in RowanStrategy

---

## Log Signatures

### Market Order Attempt
```
ERROR [RowanStrategy][ProtectionGuardian_NoSL] Emergency flatten attempt 1/3 for Buy 1 @ 6047.50
TRADING [RowanStrategy][ProtectionGuardian_NoSL] Emergency market close order placed: Sell 1 (OrderId=ABC123)
SYSTEM [RowanStrategy][ProtectionGuardian_NoSL] Position POS_001 still exists after 1s, continuing to poll...
TRADING [RowanStrategy][ProtectionGuardian_NoSL] Position POS_001 closed successfully via market order after 2s
```

### Fallback Used
```
ERROR [RowanStrategy][ProtectionGuardian_NoSL] ALL 3 MARKET ORDER ATTEMPTS FAILED - Using Position.Close() as absolute last resort
ERROR [RowanStrategy][ProtectionGuardian_NoSL] Position.Close() fallback sent for POS_001
TRADING [RowanStrategy][ProtectionGuardian_NoSL] Position POS_001 closed via Position.Close() fallback
```

### Complete Failure
```
ERROR [RowanStrategy][ProtectionGuardian_NoSL] Position.Close() fallback FAILED: [error message]
ERROR [RowanStrategy][ProtectionGuardian_NoSL] CRITICAL: Position POS_001 STILL EXISTS even after Position.Close() fallback!
```

---

## Build Status

✅ **SUCCESS**
- Strategy DLL compiled: `C:\Quantower1\Settings\Scripts\Strategies\DivergentStrV0-1\DivergentStrV0_1.dll`
- Zero compile errors
- 17 pre-existing warnings (unchanged)
- Plugin lock error (unrelated, doesn't affect strategy)

---

## Summary

**Position.Close() Usage:**
- **Before**: 4 locations in RowanStrategy
- **After**: 1 location (inside EmergencyFlattenPosition as fallback only)
- **TpSlItemPosition.Quit**: Unchanged (as requested)

**Emergency Close Mechanism:**
- **First choice**: Market order (3 attempts with verification)
- **Last resort**: Position.Close() (only after all market attempts fail)
- **Frequency**: Should be zero in normal trading
- **Protection**: Rate limiting prevents infinite loops

**All emergency closes now follow the same pattern as normal exits and reversals - market orders first, always.**

---

**Implementation Date**: November 19, 2025
**Status**: Complete and Compiled Successfully

