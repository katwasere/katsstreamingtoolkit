using System.Globalization;
using System.Xml.Linq;

namespace KatStreamToolkit.Services;

// One minute of relay traffic: the /stat endpoint's per-stream bw_in/bw_out
// (bytes per second, stock nginx-rtmp stat module) summed over every stream
// of the live application, converted to Mbps. Streams = pushed/pulled RTMP
// names, clients = publishers + subscribers (the routed encoders).
public sealed record MetricSample(DateTime UtcTime, double InMbps, double OutMbps, int Streams, int Clients);

// Relay metrics history: one /stat read per minute through the persistent SSH
// session. Deliberately CLIENT-side (in-memory ring buffer, 24h at 1/min)
// instead of the roadmap's original "on-server store": the stat data only
// exists while the relay runs, a client-side buffer needs no bundle change or
// redeploy, and the toolkit is the only consumer.
public static class RelayMetricsService
{
    public static MetricSample? FetchSample(DeployTarget t)
    {
        string xml = RelayPreviewService.RunOnServer(t,
            "curl -s --max-time 3 http://127.0.0.1:8080/stat || wget -qO- -T 3 http://127.0.0.1:8080/stat || true", 20);
        return ParseSample(xml, DateTime.UtcNow);
    }

    public static MetricSample? ParseSample(string xml, DateTime utcTime)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<rtmp")) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            double inBps = 0, outBps = 0;
            int streams = 0, clients = 0;
            foreach (var s in doc.Descendants("stream"))
            {
                streams++;
                inBps += Num(s.Element("bw_in"));
                outBps += Num(s.Element("bw_out"));
                clients += (int)Num(s.Element("nclients"));
            }
            return new MetricSample(utcTime, inBps * 8.0 / (1024 * 1024), outBps * 8.0 / (1024 * 1024), streams, clients);
        }
        catch
        {
            return null;
        }

        static double Num(XElement? e) =>
            e != null && double.TryParse(e.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
