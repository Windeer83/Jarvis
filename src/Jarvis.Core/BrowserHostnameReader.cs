using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Jarvis.Core;

internal static class BrowserHostnameReader
{
    internal static string? Read(IntPtr browserWindow)
    {
        try
        {
            var root = AutomationElement.FromHandle(browserWindow);
            var addressBars = root.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new OrCondition(
                        AutomationIdIs("view_1021"),
                        AutomationIdIs("urlbar-input"),
                        AutomationIdIs("address-bar"),
                        new PropertyCondition(
                            AutomationElement.AcceleratorKeyProperty,
                            "Ctrl+L",
                            PropertyConditionFlags.IgnoreCase),
                        new PropertyCondition(
                            AutomationElement.AccessKeyProperty,
                            "Ctrl+L",
                            PropertyConditionFlags.IgnoreCase))));

            // Never request a ValuePattern while any candidate address bar has focus: its value
            // may be an unsubmitted search term or secret rather than the current page URL.
            foreach (AutomationElement element in addressBars)
            {
                if (element.Current.HasKeyboardFocus)
                {
                    return null;
                }
            }

            foreach (AutomationElement element in addressBars)
            {
                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject))
                {
                    return null;
                }

                // Keep the raw address-bar value confined to this scope. Only hostname leaves it.
                return ParseHostname(((ValuePattern)patternObject).Current.Value);
            }

            return null;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    internal static string? ParseHostname(string addressBarValue)
    {
        var candidate = addressBarValue.Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var explicitUri) &&
            (explicitUri.Scheme == Uri.UriSchemeHttp || explicitUri.Scheme == Uri.UriSchemeHttps) &&
            TryNormalizeHostname(explicitUri, out var explicitHost))
        {
            return explicitHost;
        }

        return TryGetStrictSchemeElidedHostname(candidate, out var hostname) ? hostname : null;
    }

    private static PropertyCondition AutomationIdIs(string automationId) =>
        new(
            AutomationElement.AutomationIdProperty,
            automationId,
            PropertyConditionFlags.IgnoreCase);

    private static bool TryGetStrictSchemeElidedHostname(string candidate, out string hostname)
    {
        hostname = string.Empty;
        if (candidate.Any(char.IsWhiteSpace) || candidate.Contains('@'))
        {
            return false;
        }

        var authorityEnd = candidate.IndexOfAny('/', '?', '#');
        var authority = authorityEnd >= 0 ? candidate[..authorityEnd] : candidate;
        if (authority.Length == 0)
        {
            return false;
        }

        string host;
        if (authority[0] == '[')
        {
            var closeBracket = authority.IndexOf(']');
            if (closeBracket <= 1)
            {
                return false;
            }

            host = authority[1..closeBracket];
            var remainder = authority[(closeBracket + 1)..];
            if (remainder.Length > 0 &&
                (!remainder.StartsWith(':') || !IsValidPort(remainder[1..])) ||
                !IPAddress.TryParse(host, out _))
            {
                return false;
            }
        }
        else
        {
            var firstColon = authority.IndexOf(':');
            if (firstColon >= 0)
            {
                if (firstColon != authority.LastIndexOf(':') ||
                    !IsValidPort(authority[(firstColon + 1)..]))
                {
                    return false;
                }

                host = authority[..firstColon];
            }
            else
            {
                host = authority;
            }

            var isIpAddress = IPAddress.TryParse(host, out _);
            var isLocalhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            var isDnsHostname = host.Contains('.') &&
                                Uri.CheckHostName(host) == UriHostNameType.Dns &&
                                (!host.All(character => char.IsAsciiDigit(character) || character == '.') ||
                                 isIpAddress);
            if (!isIpAddress && !isLocalhost && !isDnsHostname)
            {
                return false;
            }
        }

        return Uri.TryCreate($"https://{candidate}", UriKind.Absolute, out var uri) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               TryNormalizeHostname(uri, out hostname);
    }

    private static bool TryNormalizeHostname(Uri uri, out string hostname)
    {
        hostname = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        return hostname.Length > 0 && uri.HostNameType != UriHostNameType.Unknown;
    }

    private static bool IsValidPort(string value) =>
        value.Length > 0 &&
        value.All(char.IsAsciiDigit) &&
        int.TryParse(value, out var port) &&
        port is >= 1 and <= 65_535;
}
