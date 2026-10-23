using System;
using System.Collections.Generic;
using System.Linq;

namespace PathHide.I18n;

/// <summary>
/// The zone user-facing times are shown in (timestamp-conventions, Time zones): <see cref="System"/>,
/// which follows the computer's zone at every launch, or one IANA zone chosen from the platform's
/// list. PathHide reads no times without a zone, so it has no interpretation zone.
/// </summary>
internal static class DisplayZone
{
    /// <summary>The saved value that follows the computer's zone.</summary>
    internal const string System = "system";

    private static readonly Lazy<IReadOnlyList<string>> s_zones = new(ReadZones);
    // Resolved once when put in effect, since every shown time reads it; one reference, so a reader
    // never pairs one preference with another's zone.
    private static volatile InEffect s_inEffect = new(System, TimeZoneInfo.Local);

    private sealed record InEffect(string Preference, TimeZoneInfo Zone);

    /// <summary>The saved preference in effect: <see cref="System"/> or an IANA zone.</summary>
    internal static string Preference => s_inEffect.Preference;

    /// <summary>The zone times are shown in now.</summary>
    internal static TimeZoneInfo Current => s_inEffect.Zone;

    /// <summary>Raised when the preference in effect changes, so shown times can be redrawn.</summary>
    internal static event Action? Changed;

    internal static void Use(string? preference)
    {
        var normalized = NormalizePreference(preference);
        if (normalized == s_inEffect.Preference)
            return;
        s_inEffect = new(normalized, Resolve(normalized));
        Changed?.Invoke();
    }

    /// <summary>
    /// The platform's IANA zones, in order. On Windows each system zone is named by its IANA
    /// equivalent; a zone without one is left out, since only an IANA name is saved.
    /// </summary>
    internal static IReadOnlyList<string> Zones => s_zones.Value;

    /// <summary><see cref="System"/> or a zone from <see cref="Zones"/>; anything else is not a preference.</summary>
    internal static string? ParsePreference(string? saved) =>
        saved == System ? System
        : saved is not null && Zones.Contains(saved, StringComparer.Ordinal) ? saved
        : null;

    internal static string NormalizePreference(string? saved) => ParsePreference(saved) ?? System;

    /// <summary>
    /// The zone a preference shows times in. System is the computer's zone, which .NET reports as UTC
    /// when the computer's own cannot be read.
    /// </summary>
    internal static TimeZoneInfo Resolve(string? preference) =>
        NormalizePreference(preference) is var normalized && normalized != System
        && TimeZoneInfo.TryFindSystemTimeZoneById(normalized, out var zone)
            ? zone
            : TimeZoneInfo.Local;

    /// <summary>
    /// The canonical IANA name of the zone System resolves to now, as its list entry shows it. On macOS
    /// it is read from the system's own setting, since .NET can name the same zone by a legacy alias.
    /// </summary>
    internal static string SystemZoneName() =>
        (OperatingSystem.IsMacOS() ? ZoneFromLink(LocaltimeLink()) : null)
        ?? IanaName(TimeZoneInfo.Local)
        ?? TimeZoneInfo.Local.Id;

    /// <summary>
    /// The zone named by a <c>/etc/localtime</c> link target: what follows its last <c>zoneinfo/</c>,
    /// as macOS's own time-zone API reads it. .NET instead matches the zone data, and reports a legacy
    /// alias such as "Japan" when the link points outside <c>/usr/share/zoneinfo</c>, as macOS's does
    /// (<c>/var/db/timezone/zoneinfo/Asia/Tokyo</c>).
    /// </summary>
    internal static string? ZoneFromLink(string? target)
    {
        const string marker = "zoneinfo/";
        var at = target?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        return at < 0 || at + marker.Length >= target!.Length ? null : target[(at + marker.Length)..];
    }

    private static string? LocaltimeLink()
    {
        try
        {
            return new System.IO.FileInfo("/etc/localtime").LinkTarget;
        }
        catch (Exception)
        {
            // Unreadable: the name .NET reports stands in.
            return null;
        }
    }

    internal static string? IanaName(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : null;

    private static IReadOnlyList<string> ReadZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(IanaName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
