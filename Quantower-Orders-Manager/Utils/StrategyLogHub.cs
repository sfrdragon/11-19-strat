using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.Utils
{
    public sealed class StrategyLogEntry
    {
        public StrategyLogEntry(DateTime timestampUtc, LoggingLevel level, string source, string message)
        {
            TimestampUtc = timestampUtc;
            Level = level;
            Source = source ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public DateTime TimestampUtc { get; }
        public LoggingLevel Level { get; }
        public string Source { get; }
        public string Message { get; }
    }

    public static class StrategyLogHub
    {
        public const int MaxEntries = 2000;

        private static readonly LinkedList<StrategyLogEntry> _entries = new();
        private static readonly object _sync = new();

        public static event Action<StrategyLogEntry> LogReceived;

        public static IReadOnlyCollection<StrategyLogEntry> GetSnapshot()
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }

        public static void Publish(string source, string message, LoggingLevel level)
        {
            var entry = new StrategyLogEntry(DateTime.UtcNow, level, source, message);

            lock (_sync)
            {
                _entries.AddLast(entry);
                if (_entries.Count > MaxEntries)
                    _entries.RemoveFirst();
            }

            LogReceived?.Invoke(entry);
        }

        public static void Forward(string source, string message, LoggingLevel level, bool keepOriginalFormat = true)
        {
            Publish(source, message, level);
            AppLog.Log(source, "Forward", message, level);
        }

        public static void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }
    }
}
