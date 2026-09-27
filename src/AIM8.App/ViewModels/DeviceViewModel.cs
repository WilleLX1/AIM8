using CommunityToolkit.Mvvm.ComponentModel;
using AIM8.Core.Devices;

namespace AIM8.App.ViewModels;

/// <summary>The Device panel: who is attached, and whether it is ready for development.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _headline = "No iPhone connected";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _iosVersion = "";
    [ObservableProperty] private string _udid = "";
    [ObservableProperty] private string _connectionKind = "";
    [ObservableProperty] private string _battery = "";
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private string _orientation = "";
    [ObservableProperty] private string _problem = "";

    [ObservableProperty] private bool? _developerMode;
    [ObservableProperty] private bool? _developerImage;
    [ObservableProperty] private bool _trusted;

    /// <summary>Shown when the phone is attached but not usable yet.</summary>
    [ObservableProperty] private string _readiness = "";

    /// <summary>Developer Mode is off, so nothing can be installed or launched.</summary>
    [ObservableProperty] private bool _needsDeveloperMode;

    /// <summary>No DeveloperDiskImage for this iOS version - the DVT services are absent.</summary>
    [ObservableProperty] private bool _needsDeveloperImage;

    /// <summary>Either of the above, i.e. the setup row has something to offer.</summary>
    [ObservableProperty] private bool _needsSetup;

    public void Update(DeviceSnapshot snapshot)
    {
        Connected = snapshot.Connected;
        Name = snapshot.Name;
        Model = snapshot.MarketingName;
        IosVersion = string.IsNullOrWhiteSpace(snapshot.IosVersion) ? "" : $"iOS {snapshot.IosVersion}";
        Udid = snapshot.Udid;
        Trusted = snapshot.Trusted;
        DeveloperMode = snapshot.DeveloperModeEnabled;
        DeveloperImage = snapshot.DeveloperImageMounted;
        Display = snapshot.DisplayResolution ?? "";
        Orientation = snapshot.Orientation ?? "";
        Problem = snapshot.Problem ?? "";

        ConnectionKind = snapshot.Connection switch
        {
            Core.Devices.ConnectionKind.Usb => "USB",
            Core.Devices.ConnectionKind.Network => "Wi-Fi",
            _ => "",
        };

        Battery = snapshot.BatteryPercent is { } percent
            ? $"{percent}%{(snapshot.Charging == true ? " charging" : "")}"
            : "";

        Headline = snapshot.Connected
            ? $"{snapshot.Name} │ {snapshot.MarketingName}"
            : "No iPhone connected";

        NeedsDeveloperMode = snapshot.Connected && snapshot.DeveloperModeEnabled == false;
        NeedsDeveloperImage = snapshot.Connected && snapshot.DeveloperImageMounted == false;
        NeedsSetup = NeedsDeveloperMode || NeedsDeveloperImage;

        Readiness = BuildReadiness(snapshot);
    }

    private static string BuildReadiness(DeviceSnapshot snapshot)
    {
        if (!snapshot.Connected) return "";
        if (snapshot.DeveloperModeEnabled == false) return "Developer Mode is off - enable it to install and launch";
        if (snapshot.DeveloperImageMounted == false) return "DeveloperDiskImage is not mounted - mount it for DVT services";
        return "";
    }
}
