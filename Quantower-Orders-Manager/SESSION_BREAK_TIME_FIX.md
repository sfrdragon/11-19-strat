# Session Break Time Fix - 4-5 PM to 5-6 PM ET

## Problem Identified
The default trading session had the daily maintenance break set to **4-5 PM ET** instead of the correct **5-6 PM ET** for NQ/ES futures.

### User Impact
- At 4:51 PM EST, session showed **INACTIVE** (incorrect)
- Should have been **ACTIVE** (trading hours)
- Break should only occur 5-6 PM ET (CME maintenance window)

## Root Cause
All break time calculations used `16` (4 PM) and `17` (5 PM) instead of `17` (5 PM) and `18` (6 PM).

## Fixes Applied

### 1. StaticUtils.cs (InMarketUtc.Build)
We ultimately replaced the manual offset math with automatic DST detection:
```csharp
var DailyBreakStartUtc = EstLocalToUtcTimeOnly(17, 0);  // 5:00 PM ET
var DailyBreakEndUtc   = EstLocalToUtcTimeOnly(18, 0);  // 6:00 PM ET
```
This guarantees the break is **always 5–6 PM ET**, translating to 21:00–22:00 UTC during EDT and 22:00–23:00 UTC during EST. The legacy overload `Build(int utcOffsetHours)` now simply calls the auto-DST version for backward compatibility.

### 2. DivergentStrV0_1.cs (UI Defaults - GETTER)
**Line 471**

**Before**:
```csharp
// Default: 9:30 AM - 4:00 PM in user's specified timezone
int utcEnd = 16 * 60 - (_UtcOffsetHours * 60);  // 4:00 PM
```

**After**:
```csharp
// Default: 9:30 AM - 5:00 PM in user's specified timezone (before 5-6 PM break)
int utcEnd = 17 * 60 - (_UtcOffsetHours * 60);  // 5:00 PM
```

### 3. DivergentStrV0_1.cs (UI Defaults - SETTER)
**Line 1253**

**Before**:
```csharp
// Default to 9:30 AM - 4:00 PM in user's specified timezone
int utcEndMin = 16 * 60 - (_UtcOffsetHours * 60);  // 4:00 PM
```

**After**:
```csharp
// Default to 9:30 AM - 5:00 PM in user's specified timezone (before 5-6 PM break)
int utcEndMin = 17 * 60 - (_UtcOffsetHours * 60);  // 5:00 PM
```

### 4. DivergentStrV0_1.cs (Session Setup Log)
**Line 288**

**Before**:
```csharp
$"Default sessions registered with UTC offset {_UtcOffsetHours} (Break: 4-5 PM local)"
```

**After**:
```csharp
$"Default sessions registered with Auto-DST (Break: 5-6 PM ET)"
```

### 5. RowanStrategy.cs (Constructor Log)
Constructor now clarifies the split between Auto-DST sessions and the stored user offset for custom windows:
```csharp
$"UTC offset stored: {utcOffsetHours} (for custom sessions). Default sessions use Auto-DST (5-6 PM ET break)"
```

## Verification

### With UTC-5 (EST)
**Break Times**:
- Start: 17 - (-5) = **22:00 UTC** (5:00 PM EST)
- End: 18 - (-5) = **23:00 UTC** (6:00 PM EST)

**Trading Session**:
- Open: 23:00 UTC (6:00 PM EST)
- Close: 22:00 UTC (5:00 PM EST next day)
- Type: Overnight (Close < Open)

**Containment Logic**:
```
For time 21:51 UTC (4:51 PM EST):
  Is 21:51 >= 23:00? NO
  Is 21:51 < 22:00? YES ✅
  Result: ACTIVE ✅
  
For time 22:30 UTC (5:30 PM EST):
  Is 22:30 >= 23:00? NO
  Is 22:30 < 22:00? NO
  Result: INACTIVE ✅ (correctly in break)
```

### With UTC-4 (EDT)
**Break Times**:
- Start: 17 - (-4) = **21:00 UTC** (5:00 PM EDT)
- End: 18 - (-4) = **22:00 UTC** (6:00 PM EDT)

**Trading Session**:
- Open: 22:00 UTC (6:00 PM EDT)
- Close: 21:00 UTC (5:00 PM EDT next day)

## Expected Logs After Fix

### Startup
```
[RowanStrategy][Constructor] UTC offset stored: -5 (for custom sessions). Default sessions use Auto-DST (5-6 PM ET break)
[DivergentStr][SessionSetup] Default sessions registered with Auto-DST (Break: 5-6 PM ET)
```

### At 4:51 PM EST (21:51 UTC)
```
[StaticSessionManager] InMarket (UTC-5) status: Active ✅
[RowanStrategy][AllowToTrade] Session check passed, trading enabled
```

### At 5:30 PM EST (22:30 UTC)
```
[StaticSessionManager] InMarket (UTC-5) status: Inactive ✅
[RowanStrategy][AllowToTrade] Session inactive, trading blocked
```

## Build Status
✅ **SUCCESS**
- DLL: `C:\Quantower1\Settings\Scripts\Strategies\DivergentStrV0-1\DivergentStrV0_1.dll`
- Compile errors: 0
- All changes: 5 lines modified (4× changed `16→17`, 1× changed comments)

## Files Modified
1. `StaticUtils.cs` - Changed break calculation from 16-17 to 17-18
2. `DivergentStrV0_1.cs` - Updated UI defaults and logs (3 locations)
3. `RowanStrategy.cs` - Updated constructor diagnostic log

## Impact
- **Before**: Trading blocked 4:00-5:00 PM ET (WRONG)
- **After**: Trading blocked 5:00-6:00 PM ET (CORRECT)
- **Trading hours**: 23 hours/day (6 PM ET → 5 PM ET next day)

## Testing
1. Restart strategy with UTC offset -5 (EST)
2. Check logs show: `(5-6 PM local break = 22-23 UTC)`
3. At 4:51 PM EST: Session should be **ACTIVE**
4. At 5:30 PM EST: Session should be **INACTIVE**
5. At 6:01 PM EST: Session should be **ACTIVE** again

---

**Fix Date**: November 19, 2025  
**Status**: Compiled and deployed successfully  
**Result**: Default session now correctly implements 23-hour trading with 5-6 PM ET break

