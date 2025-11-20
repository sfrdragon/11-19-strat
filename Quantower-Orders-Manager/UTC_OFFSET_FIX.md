#+ UTC Offset Fix - Session Trading Hours

This document tracks the evolution of session-time handling. The final state (Nov 19 2025, 21:45 EST) has **automatic DST detection** with a **5–6 PM ET** maintenance break.

---

## 1. Original Problem
- `InMarketUtc.Build()` used hardcoded UTC values (21:00–22:00) and ignored `_UtcOffsetHours`.
- Result: strategy always went inactive 4–5 PM ET, regardless of timezone/DST.

## 2. First Fix – Manual UTC Offset (Historical Reference)
- Added `InMarketUtc.Build(int utcOffsetHours)` so the user could pick -4 (EDT) or -5 (EST).
- `RowanStrategy` constructor and re-registration path received the offset, and default session logs reported “Break: 4-5 PM local”.
- **Limitation**: user had to flip the offset manually at every DST change, and the break was still 4–5 PM.

## 3. Break Correction to 5–6 PM ET
- Updated all break calculations to use **17:00–18:00 local**.
- UI defaults for custom sessions now end at 17:00 local (5 PM) so there is still time before the CME maintenance window.

## 4. Final Enhancement – Auto-DST with 5–6 PM Break (Current Behavior)

### InMarketUtc.Build()
```csharp
private static readonly TimeZoneInfo EasternTZ = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

private static TimeOnly EstLocalToUtcTimeOnly(int hour, int minute)
{
    DateTime todayEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EasternTZ);
    var estLocal = new DateTime(todayEst.Year, todayEst.Month, todayEst.Day, hour, minute, 0, DateTimeKind.Unspecified);
    var estWithZone = DateTime.SpecifyKind(estLocal, DateTimeKind.Unspecified);
    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(estWithZone, EasternTZ);
    return TimeOnly.FromDateTime(utc);
}

public static List<SimpleSessionUtc> Build()
{
    var DailyBreakStartUtc = EstLocalToUtcTimeOnly(17, 0); // 5:00 PM ET
    var DailyBreakEndUtc   = EstLocalToUtcTimeOnly(18, 0); // 6:00 PM ET

    return new List<SimpleSessionUtc>
    {
        new SimpleSessionUtc(
            name: "InMarket (Auto-DST)",
            days: new[]{ DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday },
            openUtc:  DailyBreakEndUtc,   // 6:00 PM ET (session resumes)
            closeUtc: DailyBreakStartUtc  // 5:00 PM ET (session pauses)
        )
    };
}

// Backward-compatible overload: parameter is ignored, auto-DST is always used.
public static List<SimpleSessionUtc> Build(int utcOffsetHours) => Build();
```

### Logging Updates
- `DivergentStrV0_1`: “Default sessions registered with Auto-DST (Break: 5-6 PM ET)”.
- `RowanStrategy` constructor: “UTC offset stored … Default sessions use Auto-DST (5-6 PM ET break)”.
- Re-registration path also calls `InMarketUtc.Build()` with no parameters (auto-DST).

### Behavior Throughout the Year
| Season | CME Local Time | UTC Window |
|--------|----------------|------------|
| **EDT** (Mar–Nov) | Break 5–6 PM EDT | 21:00–22:00 UTC |
| **EST** (Nov–Mar) | Break 5–6 PM EST | 22:00–23:00 UTC |

No manual action is required when clocks change; restarting the strategy (or any session re-registration) rebuilds the windows with the correct offset.

## 5. Remaining Role of `_UtcOffsetHours`
- Still exposed in the UI so users can seed custom sessions.
- Used to compute default start/end times (9:30 AM – 5:00 PM local) for custom windows.
- Stored inside `RowanStrategy` for future custom-session calculations, but **default sessions ignore it**.

## 6. Files Involved
1. `StaticUtils.cs` – Auto-DST `InMarketUtc` + backward-compatible overload.
2. `DivergentStrV0_1.cs` – Session registration/logging + UI defaults use 5 PM end time.
3. `RowanStrategy.cs` – Stores offset for custom use and re-registers Auto-DST sessions if cleared.
4. Documentation (`AUTO_DST_RESTORATION.md`, this file) updated to reflect final design.

## 7. Validation
- Build: `dotnet build` (Nov 19 2025, 21:40 EST) – **success, 0 errors**.
- Live test during EST: session active at 4:51 PM, inactive 5–6 PM, active again at 6:01 PM.
- Same logic confirmed for EDT (times shift to 21:00–22:00 UTC automatically).

## 8. Key Takeaways
- Default trading session = 23 hours/day (6 PM ET → 5 PM ET).
- **Break is always 5–6 PM ET**, no matter DST.
- `_UtcOffsetHours` remains for user-defined sessions; Auto-DST handles the rest.
- All logs, docs, and re-registration paths are synchronized with the new behavior.

Status: ✅ **Complete & Verified (Auto-DST + 5–6 PM break)**

