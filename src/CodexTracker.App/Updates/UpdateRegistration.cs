using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace CodexTracker.App.Updates;

internal static class UpdateRegistration
{
    // Inno Setup installs per user in the 64-bit registry view. Portable copies must
    // never change the registration belonging to a separately installed tracker.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C84B03F8-B16D-4A0C-9249-148C613C3581}_is1";

    internal static void Refresh(string target)
    {
        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var registration = currentUser.OpenSubKey(UninstallKey, writable: true);
            if (registration is not null)
                Refresh(registration, target, FileVersionInfo.GetVersionInfo(target).ProductVersion);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { }
    }

    internal static void Refresh(RegistryKey registration, string target, string? productVersion)
    {
        if (registration.GetValue("InstallLocation") is not string location ||
            SemanticVersion.Parse(productVersion) is not { } version ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                Path.GetDirectoryName(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase)) return;
        registration.SetValue("DisplayVersion", version.Text, RegistryValueKind.String);
        registration.SetValue("VersionMajor", version.Major, RegistryValueKind.DWord);
        registration.SetValue("VersionMinor", version.Minor, RegistryValueKind.DWord);
    }
}
