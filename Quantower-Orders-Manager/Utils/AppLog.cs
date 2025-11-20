using System;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.Utils
{
    public static class AppLog
    {
        private static void Write(string component, string reason, string message, LoggingLevel level)
        {
            var prefix = string.IsNullOrWhiteSpace(component) ? "General" : component.Trim();
            var tag = string.IsNullOrWhiteSpace(reason) ? "General" : reason.Trim();
            Core.Instance.Loggers.Log($"[{prefix}][{tag}] {message}", level);
        }

        public static void Log(string component, string reason, string message, LoggingLevel level) => Write(component, reason, message, level);
        public static void Info(string component, string reason, string message) => Write(component, reason, message, LoggingLevel.System);
        public static void System(string component, string reason, string message) => Write(component, reason, message, LoggingLevel.System);
        public static void Trading(string component, string reason, string message) => Write(component, reason, message, LoggingLevel.Trading);
        public static void Error(string component, string reason, string message) => Write(component, reason, message, LoggingLevel.Error);
        public static void Error(string component, string reason, string message, Exception ex) => Write(component, reason, $"{message} | Exception: {ex.Message}", LoggingLevel.Error);
    }
}
