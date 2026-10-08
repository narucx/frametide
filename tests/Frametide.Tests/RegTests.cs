using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Tests;

/// <summary>Writes only below HKCU\Software\FrametideTests\(guid) and deletes it afterwards.</summary>
public sealed class RegTests : IDisposable
{
    private readonly string _sub = $@"Software\FrametideTests\{Guid.NewGuid():N}";
    private string KeyPath => $@"HKCU:\{_sub}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\FrametideTests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\FrametideTests", throwOnMissingSubKey: false);
    }

    [Theory]
    [InlineData(@"HKLM:\SOFTWARE\X", RegistryHive.LocalMachine, @"SOFTWARE\X")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Y\", RegistryHive.CurrentUser, @"Software\Y")]
    [InlineData("HKCU:", RegistryHive.CurrentUser, "")]
    public void Parses_drive_style_and_native_paths(string path, RegistryHive hive, string sub)
    {
        var (root, s) = Reg.Parse(path);
        using (root)
        {
            Assert.Equal(RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).Name, root.Name);
            Assert.Equal(sub, s);
        }
    }

    [Fact]
    public void Dword_above_int_max_roundtrips_through_the_journal_format()
    {
        Reg.Set(KeyPath, "Cache", RegistryValueKind.DWord, 0xFFFFFFFFL);
        var json = Reg.Get(KeyPath, "Cache").ToJson();
        Assert.Equal(4294967295L, json["Value"]!.GetValue<long>());
        Reg.Remove(KeyPath, "Cache");
        Reg.Restore(KeyPath, "Cache", RegValue.FromJson(json));
        Assert.True(Reg.Equals(KeyPath, "Cache", RegistryValueKind.DWord, 0xFFFFFFFFL));
    }

    [Fact]
    public void Binary_string_and_multistring_roundtrip()
    {
        Reg.Set(KeyPath, "B", RegistryValueKind.Binary, new byte[] { 3, 0, 255 });
        Reg.Set(KeyPath, "S", RegistryValueKind.ExpandString, "%SystemRoot%\\x");
        Reg.Set(KeyPath, "M", RegistryValueKind.MultiString, new[] { "a", "b" });
        foreach (var name in new[] { "B", "S", "M" })
        {
            var original = Reg.Get(KeyPath, name);
            var json = original.ToJson();
            Reg.Remove(KeyPath, name);
            Reg.Restore(KeyPath, name, RegValue.FromJson(json));
            var back = Reg.Get(KeyPath, name);
            Assert.Equal(original.Kind, back.Kind);
            Assert.Equal(Reg.ToJson(original.Kind, original.Value)!.ToJsonString(), Reg.ToJson(back.Kind, back.Value)!.ToJsonString());
        }
        Assert.Equal("%SystemRoot%\\x", Reg.Get(KeyPath, "S").Value);   // not expanded
    }

    [Fact]
    public void Restoring_a_value_that_did_not_exist_removes_it()
    {
        var before = Reg.Get(KeyPath, "New");
        Assert.False(before.Exists);
        Reg.Set(KeyPath, "New", RegistryValueKind.DWord, 1);
        Reg.Restore(KeyPath, "New", RegValue.FromJson(before.ToJson()));
        Assert.False(Reg.Get(KeyPath, "New").Exists);
    }
}
