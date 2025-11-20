using System.Collections.Generic;
using System.Linq;
using System;
using TradingPlatform.BusinessLayer;
using DivergentStrV0_1.Utils;


namespace DivergentStrV0_1.Utils
{
    public static class StaticUtils
    {
        public static Period GetPeriod(HistoricalData history)
        {
            if (history.Aggregation is HistoryAggregationTime)
            {
                var agg1 = (HistoryAggregationTime)history.Aggregation;
                return agg1.Period;
            }
            //change for a new version of API, 03/11/2025
            /*else if (history.Aggregation is HistoryAggregationTickBars)
            {
                var agg1 = (HistoryAggregationTickBars)history.Aggregation;

                return new Period(BasePeriod.Tick, agg1.TicksCount);
            }*/
            else if (history.Aggregation is HistoryAggregationTick)
            {
                var agg1 = (HistoryAggregationTick)history.Aggregation;
                return new Period();// BasePeriod.Tick);//, agg1.TicksCount);
            }
            else return new Period();
        }

        public static Indicator GenerateIndicator(string indi_names, HistoricalData hd, IList<SettingItem> indi_settings = null)
        {
            if (hd == null)
                return null;

            Indicator resoult = null;
            try
            {
                var indInfo = Core.Instance.Indicators.All.First(x => x.Name == indi_names);
                Indicator indicator = Core.Instance.Indicators.CreateIndicator(indInfo);
                if (indi_settings != null)
                    indicator.Settings = indi_settings;

                resoult = indicator;
                //HACK adding Indi Here
                hd.AddIndicator(indicator);
            }
            catch (Exception ex)
            {
                AppLog.Error("StaticUtils", "IndicatorGeneration", ex.Message);
                //StrategyLogHub.Forward("StaticUtils", "Indicator Generation Failed", loggingLevel: LoggingLevel.Error);
                //StrategyLogHub.Forward("StaticUtils", $"Failed with message : {ex.Message}", loggingLevel: LoggingLevel.Error);
            }
            return resoult;
        }
    }

    public static class InMarketUtc
    {
        // Use same Eastern timezone as OffMarketUtc for automatic DST detection
        private static readonly TimeZoneInfo EasternTZ = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        
        private static TimeOnly EstLocalToUtcTimeOnly(int hour, int minute)
        {
            // Automatic DST conversion: converts EST/EDT local time to UTC based on current date
            DateTime todayEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EasternTZ);
            var estLocal = new DateTime(todayEst.Year, todayEst.Month, todayEst.Day, hour, minute, 0, DateTimeKind.Unspecified);
            var estWithZone = DateTime.SpecifyKind(estLocal, DateTimeKind.Unspecified);
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(estWithZone, EasternTZ);
            return TimeOnly.FromDateTime(utc);
        }

        /// <summary>
        /// Costruisce le finestre IN-MARKET con automatic DST detection
        /// - Domenica→Giovedì: DailyBreakEndUtc → DailyBreakStartUtc (overnight)
        /// - Daily break: 5:00 PM - 6:00 PM ET (automatically adjusts for EST/EDT)
        /// - CRITICAL: Uses 5-6 PM (17:00-18:00), NOT 4-5 PM (CME maintenance window)
        /// </summary>
        public static List<SimpleSessionUtc> Build()
        {
            // CRITICAL FIX: Break is 5:00 PM - 6:00 PM ET (was incorrectly 4-5 PM before)
            // Auto-DST: During EDT (summer) = 21:00-22:00 UTC, During EST (winter) = 22:00-23:00 UTC
            var DailyBreakStartUtc = EstLocalToUtcTimeOnly(17, 0);  // 5:00 PM ET → UTC (auto DST)
            var DailyBreakEndUtc = EstLocalToUtcTimeOnly(18, 0);    // 6:00 PM ET → UTC (auto DST)
            
            var inMarketDays = new[]
            {
                DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
                DayOfWeek.Wednesday, DayOfWeek.Thursday
            };

            return new List<SimpleSessionUtc>
            {
                new SimpleSessionUtc(
                    name: "InMarket (Auto-DST)",
                    days: inMarketDays,
                    openUtc:  DailyBreakEndUtc,   // 6:00 PM ET (auto-adjusts to UTC)
                    closeUtc: DailyBreakStartUtc  // 5:00 PM ET (auto-adjusts to UTC, overnight)
                )
            };
        }
        
        /// <summary>
        /// Overload that accepts UTC offset for backward compatibility
        /// Ignores parameter and uses automatic DST detection instead
        /// </summary>
        public static List<SimpleSessionUtc> Build(int utcOffsetHours)
        {
            // Ignore manual offset, always use auto-DST detection
            return Build();
        }

    }

        public static class OffMarketUtc
    {
        // Costruiamo le 3 sessioni TARGET richieste, convertendo orari EST in UTC:
        // 1) Regular (prev day)    09:30–17:00 EST
        // 2) Overnight (prev→curr) 18:00–04:00 EST (overnight)
        // 3) Morning (curr day)    04:00–09:29 EST
        // Nota: usiamo il timezone Windows "Eastern Standard Time" per gestire automaticamente DST.

        private static readonly TimeZoneInfo EasternTZ = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        private static TimeOnly EstLocalToUtcTimeOnly(int hour, int minute)
        {
            // Usiamo la data corrente (UTC) solo per ricavare la conversione stagionale (DST vs standard)
            // Il risultato è l'orario UTC corrispondente per l'odierna stagione.
            DateTime todayEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EasternTZ);
            var estLocal = new DateTime(todayEst.Year, todayEst.Month, todayEst.Day, hour, minute, 0, DateTimeKind.Unspecified);
            var estWithZone = DateTime.SpecifyKind(estLocal, DateTimeKind.Unspecified);
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(estWithZone, EasternTZ);
            return TimeOnly.FromDateTime(utc);
        }

        public static List<SimpleSessionUtc> Build()
        {
            // Calcolo orari UTC risultanti dalla conversione EST→UTC (sensibile al DST attuale)
            var regularOpenUtc = EstLocalToUtcTimeOnly(9, 30);
            var regularCloseUtc = EstLocalToUtcTimeOnly(17, 0);

            var overnightOpenUtc = EstLocalToUtcTimeOnly(18, 0);
            var overnightCloseUtc = EstLocalToUtcTimeOnly(4, 0); // overnight → Close <= Open in UTC in molti periodi

            var morningOpenUtc = EstLocalToUtcTimeOnly(4, 0);
            var morningCloseUtc = EstLocalToUtcTimeOnly(9, 29);

            var sessions = new List<SimpleSessionUtc>
            {
                // Regular session (prev day 09:30–17:00 EST)
                new SimpleSessionUtc(
                    name: "REGULAR (UTC)",
                    days: new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                    openUtc: regularOpenUtc,
                    closeUtc: regularCloseUtc
                ),

                // Overnight session (prev 18:00 EST → curr 04:00 EST)
                // Valida da Domenica a Giovedì (apre la sera e chiude la mattina successiva)
                new SimpleSessionUtc(
                    name: "OVERNIGHT (UTC)",
                    days: new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday },
                    openUtc: overnightOpenUtc,
                    closeUtc: overnightCloseUtc
                ),

                // Morning session (curr day 04:00–09:29 EST)
                new SimpleSessionUtc(
                    name: "MORNING (UTC)",
                    days: new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                    openUtc: morningOpenUtc,
                    closeUtc: morningCloseUtc
                )
            };

            return sessions;
        }
    }
}
