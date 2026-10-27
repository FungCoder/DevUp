using System.Globalization;
using System.Text.RegularExpressions;
using DevUp.Services;

namespace DevUp.Core;

/// <summary>从真实终端中的下载计数计算进度和速度，不推测磁盘缓存的含义。</summary>
internal sealed class WingetDownloadProgress
{
    private static readonly Regex ByteCounts = new(
        @"(?<received>\d+(?:[.,]\d+)?)\s*(?<receivedUnit>[KMGT]i?B|B)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>[KMGT]i?B|B)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BytesOnly = new(
        @"^[\s\u2580-\u259f=<>#\[\]|/\\-]*(?<received>\d+(?:[.,]\d+)?)\s*(?<receivedUnit>[KMGT]i?B|B)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private long? _received;
    private long _total;
    private long _lastChangedMs;
    private double? _speed;
    private long? _startedMs;
    public bool HasDownloadData
    {
        get { lock (_gate) return _received.HasValue; }
    }

    public void Reset(long? startedMs = null)
    {
        lock (_gate) { _received = null; _total = 0; _speed = null; _lastChangedMs = 0; _startedMs = startedMs; }
    }

    public bool Observe(string line, long elapsedMs)
    {
        var match = ByteCounts.Match(line);
        if (!match.Success) match = BytesOnly.Match(line);
        if (!match.Success) return false;
        var received = ParseBytes(match.Groups["received"].Value, match.Groups["receivedUnit"].Value);
        var total = match.Groups["total"].Success
            ? ParseBytes(match.Groups["total"].Value, match.Groups["totalUnit"].Value) : 0;
        if (received < 0 || total < 0 || (total > 0 && received > total)) return false;

        lock (_gate)
        {
            if (!_received.HasValue)
                _speed = _startedMs.HasValue && elapsedMs > _startedMs.Value
                    ? received * 1000.0 / (elapsedMs - _startedMs.Value) : null;
            else if (received < _received.Value || total != _total)
            {
                _speed = null;
                _startedMs = elapsedMs;
            }
            else if (received > _received.Value && elapsedMs > _lastChangedMs)
            {
                var measured = (received - _received.Value) * 1000.0 / (elapsedMs - _lastChangedMs);
                _speed = _speed is > 0 && elapsedMs - _lastChangedMs <= 5000
                    ? _speed * 0.7 + measured * 0.3 : measured;
            }
            if (_received != received) _lastChangedMs = elapsedMs;
            _received = received;
            _total = total;
        }
        return true;
    }

    public ProgressInfo GetProgress(long elapsedMs, bool stalled, bool completed = false)
    {
        lock (_gate)
        {
            var speed = _speed;
            var stale = elapsedMs - _lastChangedMs > 5000;
            var speedText = speed.HasValue
                ? $"速度 {SoftwareService.FmtBytes((long)speed.Value)}/s"
                : "速度 —";
            var received = completed && _total > 0 ? _total : _received.GetValueOrDefault();
            var count = _received.HasValue
                ? _total > 0 ? $"{SoftwareService.FmtBytes(received)} / {SoftwareService.FmtBytes(_total)}"
                    : $"已下载 {SoftwareService.FmtBytes(received)}"
                : "等待下载数据";
            return new ProgressInfo
            {
                Percent = _received.HasValue && _total > 0 ? completed ? 100 : Math.Min(99, received * 100.0 / _total) : -1,
                Status = stalled ? "正在下载，等待进度刷新…" : "正在下载安装包…",
                Detail = speedText,
                Metric = count,
                DetailHint = speed.HasValue && stale ? "最近一次测得的下载速度，等待下载器更新。"
                    : _received.HasValue ? "根据 winget 实时下载计数计算。" : "正在等待下载数据。"
            };
        }
    }

    private static long ParseBytes(string value, string unit)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount)) return -1;
        var exponent = "BKMGT".IndexOf(char.ToUpperInvariant(unit[0]));
        var bytes = amount * Math.Pow(1024, exponent);
        return bytes >= 0 && bytes < long.MaxValue ? (long)bytes : -1;
    }
}
