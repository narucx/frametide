using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Games;

/// <summary>A CS2 relay region (Steam Datagram Relay point of presence).</summary>
public sealed record RelayRegion(string Code, string Description, IReadOnlyList<string> Addresses, bool Blocked, int? PingMs);

/// <summary>
/// Blocks CS2 relay regions through the Windows Firewall (outbound rules in their own group), so matchmaking does not
/// send you to far-away servers. Uses the firewall's COM API, the same rules as "Windows Defender Firewall with
/// Advanced Security" shows.
/// </summary>
public static partial class ServerBlocker
{
    public const string Group = "Frametide CS2 Server Blocker";
    private const string RulePrefix = "Frametide CS2 ";

    /// <summary>Relay regions from Steam's public SDR configuration, with a ping to the first relay of each.</summary>
    public static async Task<IReadOnlyList<RelayRegion>> LoadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Frametide", "1.0"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730"));
        var blocked = BlockedCodes();
        var regions = new List<(string Code, string Desc, List<string> Ips)>();
        foreach (var pop in doc.RootElement.GetProperty("pops").EnumerateObject())
        {
            if (!ValidCode().IsMatch(pop.Name) || !pop.Value.TryGetProperty("relays", out var relays)) continue;
            var ips = relays.EnumerateArray()
                .Select(r => r.TryGetProperty("ipv4", out var ip) ? ip.GetString() : null)
                .Where(ip => ip is not null && System.Net.IPAddress.TryParse(ip, out _)).Select(ip => ip!).ToList();
            if (ips.Count == 0) continue;
            regions.Add((pop.Name, pop.Value.TryGetProperty("desc", out var d) ? d.GetString() ?? pop.Name : pop.Name, ips));
        }
        var pings = await Task.WhenAll(regions.Select(async r =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(r.Ips[0], 1000);
                return reply.Status == IPStatus.Success ? (int?)reply.RoundtripTime : null;
            }
            catch (PingException) { return null; }
        }));
        return regions.Select((r, i) => new RelayRegion(r.Code, r.Desc, r.Ips, blocked.Contains(r.Code), pings[i]))
            .OrderBy(r => r.PingMs ?? int.MaxValue).ToList();
    }

    /// <summary>Region codes that have a blocking rule.</summary>
    public static HashSet<string> BlockedCodes()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RuleNames()) codes.Add(name[RulePrefix.Length..]);
        return codes;
    }

    /// <summary>Blocks exactly the given regions; rules for all others are removed.</summary>
    public static void Apply(IEnumerable<RelayRegion> regions, ISet<string> block)
    {
        dynamic policy = Policy();
        var existing = RuleNames().ToHashSet();   // enumerating all firewall rules is slow: once
        foreach (var r in regions)
        {
            var name = RulePrefix + r.Code;
            var has = existing.Contains(name);
            if (block.Contains(r.Code) == has) continue;
            if (has)
            {
                policy.Rules.Remove(name);
                Log.Ok($"CS2 region unblocked: {r.Description} ({r.Code}).");
                continue;
            }
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            rule.Name = name;
            rule.Grouping = Group;
            rule.Description = $"Blocks CS2 relay region {r.Description}. Created by Frametide.";
            rule.Direction = 2;               // NET_FW_RULE_DIR_OUT
            rule.Action = 0;                  // NET_FW_ACTION_BLOCK
            rule.Profiles = 0x7FFFFFFF;       // all profiles
            rule.RemoteAddresses = string.Join(",", r.Addresses);
            rule.Enabled = true;
            policy.Rules.Add(rule);
            Log.Ok($"CS2 region blocked: {r.Description} ({r.Code}).");
        }
    }

    public static void UnblockAll()
    {
        dynamic policy = Policy();
        foreach (var name in RuleNames()) policy.Rules.Remove(name);
        Log.Ok("All CS2 server blocks removed.");
    }

    private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static List<string> RuleNames()
    {
        var names = new List<string>();
        foreach (dynamic rule in Policy().Rules)
        {
            if ((string?)rule.Grouping == Group && rule.Name is string name && name.StartsWith(RulePrefix, StringComparison.Ordinal)) names.Add(name);
        }
        return names;
    }

    [GeneratedRegex("^[a-z0-9]{2,8}$")]
    private static partial Regex ValidCode();
}
