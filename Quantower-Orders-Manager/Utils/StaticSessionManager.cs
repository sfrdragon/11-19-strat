using DivergentStrV0_1.OperationSystemAdv.DDDCore;
using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;
using DivergentStrV0_1.Utils;

namespace DivergentStrV0_1.Utils
{
    /// <summary>
    /// Item singolo di livello (nome + high/low).
    /// Include il "PrevDay" come Name = "__PREV_DAY__" per distinguerlo dalle sessioni.
    /// </summary>
    public sealed class TPLevelItem
    {
        public string Name { get; }
        public double High { get; }
        public double Low { get; }
        public DayOfWeek Day{ get; set; }

        public TPLevelItem(string name, double high, double low)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "UNNAMED" : name;
            High = high;
            Low = low;
        }
    }

    /// <summary>
    /// DTO contenente tutti i livelli calcolati (prev day + target sessions).
    /// </summary>
    public sealed class TPLevelsDto
    {
        public IReadOnlyList<TPLevelItem> Levels { get; }

        public TPLevelsDto(List<TPLevelItem> levels = null)
        {
            Levels = levels ?? new List<TPLevelItem>();
        }

        /// <summary>Helper per recuperare un item per nome.</summary>
        public TPLevelItem? GetByName(string name) =>
            Levels.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public enum SessionType
    {
        Trade,
        Target
    }


    //📝 TODO: [Logs]


    public static class StaticSessionManager
    {
        public static List<SimpleSessionUtc> TargetSessions { get; set; } = new();
        public static List<SimpleSessionUtc> TradeSessions { get; set; } = new();

        private static HystoryDataProvider _dataProvider;
        public static TPLevelsDto TpLevels { get; private set; } = new TPLevelsDto();
        public static bool IsInitialized => _dataProvider != null;
        public static event EventHandler<Status> TradeSessionsStatusChanged;
        public static Status CurrentStatus
        {
            get
            {
                if (TradeSessions.Any(s => s.Status == Status.Active))
                    return Status.Active;
                return Status.Inactive;
            }
        }

        public static void Initialize(HystoryDataProvider dataProvider)
        {
            _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        }

        public static void AddSession(SimpleSessionUtc s, SessionType t)
        {
            if (t == SessionType.Target)
            {
                TargetSessions.Add(s);
                s.StatusChanged += StaticSessionManager.OnSessionStatusChanged;
            }
            else 
            {
                s.StatusChanged += StaticSessionManager.OnTradeSessionStatusChanged;
                TradeSessions.Add(s); 
            }

                
        }

        private static void OnTradeSessionStatusChanged(object sender, Status e)
        {
            TradeSessionsStatusChanged?.Invoke(sender, e);
        }

        private static void OnSessionStatusChanged(object sender, Status e)
        { 
            CalculateTPLevels();
        }

        public static void Update(IHistoryItem item)
        {
            foreach (var s in TargetSessions) s.UpdateStatus(item);
            foreach (var s in TradeSessions) s.UpdateStatus(item);
        }

        public static void RemoveSession(string name, SessionType t)
        {
            var list = (t == SessionType.Target) ? TargetSessions : TradeSessions;
            var found = list.FirstOrDefault(x => x.Name == name);
            if (found != null)
            {
                if (t == SessionType.Target)
                    found.StatusChanged -= StaticSessionManager.OnSessionStatusChanged;
                else
                    found.StatusChanged -= StaticSessionManager.OnTradeSessionStatusChanged;
                list.Remove(found);
            }
        }


        //🧠 HINT: [Dispose Called]

        public static void Dispose()
        {
            foreach (var s in TargetSessions)
                s.StatusChanged -= StaticSessionManager.OnSessionStatusChanged;
            foreach (var s in TradeSessions)
                s.StatusChanged -= StaticSessionManager.OnTradeSessionStatusChanged;
            TargetSessions.Clear();
            
            TradeSessions.Clear();
        }

        /// <summary>
        /// Calcola i livelli TP usando le 3 sessioni TARGET (REGULAR/OVERNIGHT/MORNING) in un
        /// rolling window di 24h rispetto all'ultimo item disponibile.
        /// Pubblica esattamente 3 massimi più recenti e 3 minimi più recenti come 6 TPLevelItem:
        /// - HIGH#1..#3 con Low = double.NaN
        /// - LOW#1..#3 con High = double.NaN
        /// </summary>
        public static void CalculateTPLevels()
        {
            HistoricalData hd = _dataProvider?.HistoricalData;
            if (hd == null)
                throw new ArgumentNullException(nameof(hd));
            if (hd.Symbol == null)
                throw new InvalidOperationException("HistoricalData.Symbol is null.");

            // Richiede aggregazione time-based
            if (hd.Aggregation is not HistoryAggregationTime agg)
                throw new InvalidOperationException("Expected time-based aggregation for TP levels.");

            var period = agg.Period;
            var symbol = hd.Symbol;

            // Finestra di riferimento [windowStart, now]
            DateTime nowUtc = EnsureUtc(hd[0].TimeLeft);

            // Raccogliamo le finestre di sessione che INTERSECANO la rolling window.
            // Basteranno pochi giorni intorno a now (fino a 3 giorni per sicurezza).
            var windows = new List<(DateTime Start, DateTime End, string Name, double High, double Low)>();

            foreach (var sess in TargetSessions)
            {
                if (sess == null) continue;

                // Itera su 3 giorni: oggi, ieri, l'altro ieri
                for (int d = 0; d <= 2; d++)
                {
                    var day = DateOnly.FromDateTime(nowUtc).AddDays(-d);
                    var maybe = sess.WindowForDayUtc(day);
                    if (maybe is null) continue;
                    var (startUtc, endUtc) = maybe.Value;

                    var rangeEnd = endUtc > nowUtc ? nowUtc : endUtc;

                    try
                    {
                        using var h = symbol.GetHistory(period, startUtc, rangeEnd);
                        if (h != null && h.Count > 0)
                        {
                            double hi = h.High();
                            double lo = h.Low();
                            
                            if (sess.Days.Contains(rangeEnd.DayOfWeek))
                                windows.Add((startUtc, endUtc, sess.Name ?? "UNNAMED", hi, lo));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("SessionManager", "TPLevelHistory", $"History fetch failed for {sess.Name} {startUtc:o}-{rangeEnd:o}", ex);
                    }
                }
            }

            // Ordina per fine finestra (più recente prima)
            var ordered = windows
                .OrderByDescending(w => w.End)
                .ToList();

            // Seleziona 3 HIGH più recenti e 3 LOW più recenti
            var Items = new List<TPLevelItem>();

            foreach (var w in ordered)
            {
                if (Items.Count < 3 && !double.IsNaN(w.High) && !double.IsInfinity(w.High))
                    Items.Add(new TPLevelItem($"HIGH {w.Name} {w.End:yyyy-MM-dd}", w.High, w.Low));
                
                if (Items.Count >= 3)
                    break;
            }

            TpLevels = new TPLevelsDto(Items);
        }

        private static DateTime EnsureUtc(DateTime dt) =>
            dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
}
