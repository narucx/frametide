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
                .Select(r => r.TryGetProperty("ipv4", out var ip) && ip.ValueKind == JsonValueKind.String ? PublicIPv4(ip.GetString()!) : null)
                .OfType<string>().Distinct().ToList();
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
        foreach (var name in Rules().Keys) codes.Add(name[RulePrefix.Length..]);
        return codes;
    }

    /// <summary>
    /// Blocks exactly the given regions; rules for all others are removed, also rules of regions that are no longer in
    /// Steam's list. Rules of regions whose relay addresses changed get the new addresses.
    /// </summary>
    public static void Apply(IReadOnlyList<RelayRegion> regions, ISet<string> block)
    {
        if (regions.Count == 0) throw new InvalidOperationException("The CS2 server list is not loaded.");
        dynamic policy = Policy();
        var existing = Rules();   // enumerating all firewall rules is slow: once
        var known = regions.Select(r => RulePrefix + r.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, _) in existing.Where(e => !known.Contains(e.Key)))
        {
            policy.Rules.Remove(name);
            Log.Ok($"CS2 region {name[RulePrefix.Length..]} unblocked: no longer in Steam's server list.");
        }
        foreach (var r in regions)
        {
            var name = RulePrefix + r.Code;
            var addresses = string.Join(",", r.Addresses.Select(a => a + "/255.255.255.255"));
            var has = existing.TryGetValue(name, out var current);
            if (!block.Contains(r.Code))
            {
                if (!has) continue;
                policy.Rules.Remove(name);
                Log.Ok($"CS2 region unblocked: {r.Description} ({r.Code}).");
                continue;
            }
            if (has)
            {
                if (current == addresses) continue;
                policy.Rules.Item(name).RemoteAddresses = addresses;
                Log.Ok($"CS2 region {r.Description} ({r.Code}): relay addresses updated.");
                continue;
            }
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            rule.Name = name;
            rule.Grouping = Group;
            rule.Description = $"Blocks CS2 relay region {r.Description}. Created by Frametide.";
            rule.Direction = 2;               // NET_FW_RULE_DIR_OUT
            rule.Action = 0;                  // NET_FW_ACTION_BLOCK
            rule.Profiles = 0x7FFFFFFF;       // all profiles
            rule.RemoteAddresses = addresses;
            rule.Enabled = true;
            policy.Rules.Add(rule);
            Log.Ok($"CS2 region blocked: {r.Description} ({r.Code}).");
        }
    }

    /// <summary>A public unicast IPv4 address in canonical form, or null: a bad feed must not block the LAN or everything.</summary>
    internal static string? PublicIPv4(string text)
    {
        text = text.Trim();
        if (!System.Net.IPAddress.TryParse(text, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var canonical = addr.ToString();
        if (canonical != text) return null;   // no "1", "0x7f.1" or other shorthand forms
        var b = addr.GetAddressBytes();
        var bad = b[0] is 0 or 10 or 127 or >= 224
                  || b[0] == 100 && b[1] is >= 64 and < 128      // carrier-grade NAT
                  || b[0] == 169 && b[1] == 254
                  || b[0] == 172 && b[1] is >= 16 and < 32
                  || b[0] == 192 && b[1] == 168;
        return bad ? null : canonical;
    }

    public static void UnblockAll()
    {
        dynamic policy = Policy();
        foreach (var name in Rules().Keys) policy.Rules.Remove(name);
        Log.Ok("All CS2 server blocks removed.");
    }

    private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    /// <summary>Frametide's rules: name to remote addresses.</summary>
    private static Dictionary<string, string> Rules()
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (dynamic rule in Policy().Rules)
        {
            if ((string?)rule.Grouping == Group && rule.Name is string name && name.StartsWith(RulePrefix, StringComparison.Ordinal))
                rules[name] = (string?)rule.RemoteAddresses ?? "";
        }
        return rules;
    }

    [GeneratedRegex("^[a-z0-9]{2,8}$")]
    private static partial Regex ValidCode();
}
