using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading;
using TradingPlatform.BusinessLayer;
using TradingPlatform.PresentationLayer.Plugins;
using DivergentStrV0_1.Utils;

namespace StrategyLogPlugin
{
    public class StrategyLogPlugin : Plugin
    {
        private readonly List<StrategyLogEntry> _allLogs = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly object _sync = new();
        private SynchronizationContext? _uiContext;
        private LoggingLevel? _levelFilter;
        private string _searchFilter = string.Empty;
        private bool _autoScroll = true;
        private const int MaxClientRows = 500;

        public static PluginInfo GetInfo()
        {
            return new PluginInfo()
            {
                Name = "StrategyLogPlugin",
                Title = "Strategy Log Viewer",
                Group = PluginGroup.Misc,
                ShortName = "Logs",
                TemplateName = "layout.html",
                WindowParameters = new NativeWindowParameters(NativeWindowParameters.Panel)
                {
                    AllowsTransparency = true,
                    ResizeMode = NativeResizeMode.CanResize,
                    HeaderVisible = true,
                    BindingBehaviour = BindingBehaviour.Bindable,
                    AllowCloseButton = true,
                    AllowFullScreenButton = false,
                    WindowDefaultPositionType = NativeWindowDefaultPositionType.CenterScreen,
                    AllowActionsButton = false,
                    AllowMaximizeButton = false,
                    StickingEnabled = StickyWindowBehavior.AllowSticking,
                },
                CustomProperties = new Dictionary<string, object>()
                {
                    { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                }
            };
        }

        public override Size DefaultSize => new Size(640, 420);

        public override void Initialize()
        {
            base.Initialize();

            _uiContext = SynchronizationContext.Current;

            RegisterBrowserEvents();

            lock (_sync)
            {
                _allLogs.Clear();
                _allLogs.AddRange(StrategyLogHub.GetSnapshot());
            }

            StrategyLogHub.LogReceived += OnLogReceived;
            PushStateToBrowser();
        }

        public override void Dispose()
        {
            StrategyLogHub.LogReceived -= OnLogReceived;
            base.Dispose();
        }

        private void RegisterBrowserEvents()
        {
            var browser = this.Window.Browser;
            browser.AddEventHandler("levelFilter", "onchange", OnLevelFilterChanged);
            browser.AddEventHandler("searchInput", "oninput", OnSearchTextChanged);
            browser.AddEventHandler("clearLogsButton", "onclick", OnClearLogsRequested);
            browser.AddEventHandler("refreshLogsButton", "onclick", OnRefreshRequested);
            browser.AddEventHandler("autoScrollToggle", "onchange", OnAutoScrollChanged);
        }

        private void OnLogReceived(StrategyLogEntry entry)
        {
            RunOnUi(() =>
            {
                lock (_sync)
                {
                    _allLogs.Add(entry);
                    var overflow = _allLogs.Count - StrategyLogHub.MaxEntries;
                    if (overflow > 0)
                        _allLogs.RemoveRange(0, overflow);
                }

                PushStateToBrowser();
            });
        }

        private void PushStateToBrowser()
        {
            List<StrategyLogEntry> snapshot;
            lock (_sync)
            {
                snapshot = _allLogs.ToList();
            }

            var filtered = snapshot
                .Where(MatchFilter)
                .OrderByDescending(e => e.TimestampUtc)
                .Take(MaxClientRows)
                .OrderBy(e => e.TimestampUtc)
                .Select(entry => new LogEntryViewModel(entry))
                .ToList();

            var json = JsonSerializer.Serialize(filtered, _jsonOptions);
            this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs, $"window.strategyLogs.replaceLogs({json});");

            var stats = JsonSerializer.Serialize(new
            {
                total = snapshot.Count,
                shown = filtered.Count,
                level = _levelFilter?.ToString() ?? "All",
                search = _searchFilter
            }, _jsonOptions);
            this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs, $"window.strategyLogs.updateStatus({stats});");

            this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs, $"window.strategyLogs.setAutoScroll({_autoScroll.ToString().ToLowerInvariant()});");
        }

        private bool MatchFilter(StrategyLogEntry entry)
        {
            if (_levelFilter.HasValue && entry.Level != _levelFilter.Value)
                return false;

            if (!string.IsNullOrWhiteSpace(_searchFilter))
            {
                var needle = _searchFilter.AsSpan().Trim();
                if (!entry.Message.AsSpan().Contains(needle, StringComparison.OrdinalIgnoreCase)
                    && !entry.Source.AsSpan().Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private void OnLevelFilterChanged(string elementId, object args)
        {
            var raw = this.Window.Browser.GetHtmlValue("levelFilter", HtmlGetValueAction.GetProperty, "value").Result?.ToString();
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                _levelFilter = null;
            }
            else if (Enum.TryParse(raw, true, out LoggingLevel parsed))
            {
                _levelFilter = parsed;
            }

            PushStateToBrowser();
        }

        private void OnSearchTextChanged(string elementId, object args)
        {
            var raw = this.Window.Browser.GetHtmlValue("searchInput", HtmlGetValueAction.GetProperty, "value").Result?.ToString();
            _searchFilter = raw ?? string.Empty;
            PushStateToBrowser();
        }

        private void OnClearLogsRequested(string elementId, object args)
        {
            StrategyLogHub.Clear();
            lock (_sync)
            {
                _allLogs.Clear();
            }
            PushStateToBrowser();
        }

        private void OnRefreshRequested(string elementId, object args)
        {
            lock (_sync)
            {
                _allLogs.Clear();
                _allLogs.AddRange(StrategyLogHub.GetSnapshot());
            }
            PushStateToBrowser();
        }

        private void OnAutoScrollChanged(string elementId, object args)
        {
            var state = this.Window.Browser.GetHtmlValue("autoScrollToggle", HtmlGetValueAction.GetProperty, "checked").Result;
            _autoScroll = state is bool flag ? flag : string.Equals(state?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            PushStateToBrowser();
        }

        private void RunOnUi(Action action)
        {
            if (_uiContext == null || SynchronizationContext.Current == _uiContext)
            {
                action();
            }
            else
            {
                _uiContext.Post(_ => action(), null);
            }
        }

        private sealed record LogEntryViewModel
        {
            public LogEntryViewModel(StrategyLogEntry entry)
            {
                Timestamp = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
                Level = entry.Level.ToString();
                Source = entry.Source;
                Message = entry.Message;
            }

            public string Timestamp { get; }
            public string Level { get; }
            public string Source { get; }
            public string Message { get; }
        }
    }
}
