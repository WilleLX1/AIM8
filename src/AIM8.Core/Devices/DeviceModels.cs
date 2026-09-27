namespace AIM8.Core.Devices;

public enum ConnectionKind
{
    Unknown,
    Usb,
    Network,
}

/// <summary>One entry from `usbmux list`.</summary>
public sealed record DeviceSummary(
    string Udid,
    string Name,
    string ProductType,
    string ProductVersion,
    string BuildVersion,
    ConnectionKind Connection)
{
    public string MarketingName => DeviceNames.Resolve(ProductType);

    /// <summary>iOS major version, used to decide whether developer services need a tunnel.</summary>
    public int MajorVersion =>
        int.TryParse(ProductVersion.Split('.').FirstOrDefault(), out var major) ? major : 0;

    public override string ToString() => $"{Name} ({MarketingName}, iOS {ProductVersion})";
}

/// <summary>Everything the Device panel shows, gathered once per poll.</summary>
public sealed record DeviceSnapshot
{
    public static readonly DeviceSnapshot Disconnected = new();

    public bool Connected { get; init; }

    public string Udid { get; init; } = "";

    public string Name { get; init; } = "";

    public string ProductType { get; init; } = "";

    public string IosVersion { get; init; } = "";

    public string BuildVersion { get; init; } = "";

    public ConnectionKind Connection { get; init; } = ConnectionKind.Unknown;

    /// <summary>True once lockdown answers, which only happens for a trusted pairing.</summary>
    public bool Trusted { get; init; }

    public bool? DeveloperModeEnabled { get; init; }

    public bool? DeveloperImageMounted { get; init; }

    public int? BatteryPercent { get; init; }

    public bool? Charging { get; init; }

    public string? DisplayResolution { get; init; }

    public string? Orientation { get; init; }

    /// <summary>Why the device could not be read, when Connected is false.</summary>
    public string? Problem { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string MarketingName => DeviceNames.Resolve(ProductType);

    public int MajorVersion => int.TryParse(IosVersion.Split('.').FirstOrDefault(), out var major) ? major : 0;

    /// <summary>iOS 17 moved the developer services behind an RSD tunnel.</summary>
    public bool RequiresTunnel => MajorVersion >= 17;
}

/// <summary>
/// Maps Apple product identifiers to the names people actually use. Unknown
/// identifiers are shown as-is rather than guessed at.
/// </summary>
public static class DeviceNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iPhone10,3"] = "iPhone X",
        ["iPhone10,6"] = "iPhone X",
        ["iPhone11,2"] = "iPhone XS",
        ["iPhone11,6"] = "iPhone XS Max",
        ["iPhone11,8"] = "iPhone XR",
        ["iPhone12,1"] = "iPhone 11",
        ["iPhone12,3"] = "iPhone 11 Pro",
        ["iPhone12,5"] = "iPhone 11 Pro Max",
        ["iPhone12,8"] = "iPhone SE (2nd gen)",
        ["iPhone13,1"] = "iPhone 12 mini",
        ["iPhone13,2"] = "iPhone 12",
        ["iPhone13,3"] = "iPhone 12 Pro",
        ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,6"] = "iPhone SE (3rd gen)",
        ["iPhone14,4"] = "iPhone 13 mini",
        ["iPhone14,5"] = "iPhone 13",
        ["iPhone14,2"] = "iPhone 13 Pro",
        ["iPhone14,3"] = "iPhone 13 Pro Max",
        ["iPhone14,7"] = "iPhone 14",
        ["iPhone14,8"] = "iPhone 14 Plus",
        ["iPhone15,2"] = "iPhone 14 Pro",
        ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15",
        ["iPhone15,5"] = "iPhone 15 Plus",
        ["iPhone16,1"] = "iPhone 15 Pro",
        ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,3"] = "iPhone 16",
        ["iPhone17,4"] = "iPhone 16 Plus",
        ["iPhone17,1"] = "iPhone 16 Pro",
        ["iPhone17,2"] = "iPhone 16 Pro Max",
        ["iPhone17,5"] = "iPhone 16e",
        ["iPhone18,3"] = "iPhone 17",
        ["iPhone18,4"] = "iPhone 17 Plus",
        ["iPhone18,1"] = "iPhone 17 Pro",
        ["iPhone18,2"] = "iPhone 17 Pro Max",
        ["iPad14,3"] = "iPad Pro 11 (M2)",
        ["iPad14,5"] = "iPad Pro 12.9 (M2)",
        ["iPad16,3"] = "iPad Pro 11 (M4)",
        ["iPad16,5"] = "iPad Pro 13 (M4)",
    };

    public static string Resolve(string? productType)
    {
        if (string.IsNullOrWhiteSpace(productType)) return "";
        return Names.TryGetValue(productType, out var name) ? name : productType;
    }
}
