using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Profiles;

namespace SignalForge.Core.Tests.Profiles;

public sealed class Terminal2ProfileLifecycleTests
{
    [Fact]
    public void Initialize_creates_default_profile_and_persists_terminal2_active_marker()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        var lifecycle = new Terminal2ProfileLifecycle(collection);

        var initialized = lifecycle.Initialize();

        Assert.True(initialized.IsValid, string.Join(Environment.NewLine, initialized.Issues));
        Assert.Equal("Default", initialized.Value!.Name);
        Assert.True(initialized.Value.SendCR);
        Assert.True(initialized.Value.ShowHex);
        Assert.Equal(@".\Logs", initialized.Value.LogDirectory);
        Assert.Equal("COM3", initialized.Value.SerialPort);
        Assert.Equal(new[] { "Default" }, collection.List().Value);
        Assert.Equal("Default", File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));
    }

    [Fact]
    public void Initialize_restores_saved_profile_and_updates_marker_after_profile_switch()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Create(new Terminal2Profile { Name = "Default" }).IsValid);
        Assert.True(collection.Create(new Terminal2Profile
        {
            Name = "Pump Station",
            TcpConnectAddress = "192.0.2.9",
            ConnectionType = Terminal2ConnectionType.TCPClient,
        }).IsValid);
        var savedProfile = collection.Load("Pump Station");
        Assert.True(savedProfile.IsValid, string.Join(Environment.NewLine, savedProfile.Issues));
        File.WriteAllText(Path.Combine(collection.DirectoryPath, "Active"), "Pump Station");
        var lifecycle = new Terminal2ProfileLifecycle(collection);

        var restored = lifecycle.Initialize();

        Assert.True(restored.IsValid, string.Join(Environment.NewLine, restored.Issues));
        Assert.Equal("Pump Station", restored.Value!.Name);
        Assert.Equal("192.0.2.9", restored.Value.TcpConnectAddress);
        Assert.Equal("Pump Station", File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));

        var switched = lifecycle.SetActive("Default");

        Assert.True(switched.IsValid, string.Join(Environment.NewLine, switched.Issues));
        Assert.Equal("Default", switched.Value!.Name);
        Assert.Equal("Default", File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));
    }

    [Theory]
    [InlineData("Missing profile")]
    [InlineData("../outside")]
    [InlineData("")]
    public void Initialize_falls_back_to_default_for_missing_unsafe_or_empty_active_name(string marker)
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Create(new Terminal2Profile { Name = "Default" }).IsValid);
        File.WriteAllText(Path.Combine(collection.DirectoryPath, "Active"), marker);

        var initialized = new Terminal2ProfileLifecycle(collection).Initialize();

        Assert.True(initialized.IsValid, string.Join(Environment.NewLine, initialized.Issues));
        Assert.Equal("Default", initialized.Value!.Name);
        Assert.Contains(initialized.Issues, issue => issue.Code is "profile.active-fallback" or "profile.active-marker-empty");
        Assert.Equal("Default", File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "outside.profile")));
    }

    [Fact]
    public void Initialize_does_not_overwrite_an_existing_invalid_default_profile()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Directory.CreateDirectory(collection.DirectoryPath);
        var path = Path.Combine(collection.DirectoryPath, "Default.profile");
        const string invalid = "[Profile]\nSendCR=not-a-boolean\n";
        File.WriteAllText(path, invalid);

        var initialized = new Terminal2ProfileLifecycle(collection).Initialize();

        Assert.False(initialized.IsValid);
        Assert.Contains(initialized.Issues, issue => issue.Code == "profile.invalid-boolean");
        Assert.Equal(invalid, File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(collection.DirectoryPath, "Active")));
    }

    [Fact]
    public void Initialize_restores_valid_saved_profile_when_unused_default_is_invalid()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Create(new Terminal2Profile { Name = "Pump Station" }).IsValid);
        Directory.CreateDirectory(collection.DirectoryPath);
        var invalidDefault = Path.Combine(collection.DirectoryPath, "Default.profile");
        const string invalid = "[Profile]\nSendCR=not-a-boolean\n";
        File.WriteAllText(invalidDefault, invalid);
        File.WriteAllText(Path.Combine(collection.DirectoryPath, "Active"), "Pump Station");

        var initialized = new Terminal2ProfileLifecycle(collection).Initialize();

        Assert.True(initialized.IsValid, string.Join(Environment.NewLine, initialized.Issues));
        Assert.Equal("Pump Station", initialized.Value!.Name);
        Assert.Equal("Pump Station", File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));
        Assert.Equal(invalid, File.ReadAllText(invalidDefault));
    }

    [Fact]
    public void SetActive_only_persists_profiles_that_load_successfully()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Create(new Terminal2Profile { Name = "Default" }).IsValid);
        var lifecycle = new Terminal2ProfileLifecycle(collection);
        Assert.True(lifecycle.Initialize().IsValid);
        var priorMarker = File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active"));

        var result = lifecycle.SetActive("../outside");

        Assert.False(result.IsValid);
        Assert.Equal("profile.invalid-name", result.Issues.Single().Code);
        Assert.Equal(priorMarker, File.ReadAllText(Path.Combine(collection.DirectoryPath, "Active")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "signalforge-profile-lifecycle-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
