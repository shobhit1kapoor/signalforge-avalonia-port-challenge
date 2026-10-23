/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2ProfileCollectionTests
{
    [Fact]
    public void Missing_directory_is_an_empty_collection_and_is_not_created_by_list()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Path.Combine(temporary.Path, "profiles");
        var collection = new Terminal2ProfileCollection(directory);

        var result = collection.List();

        Assert.True(result.IsValid);
        Assert.Empty(result.Value!);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Save_load_and_list_use_the_existing_serializer_and_filename_name_precedence()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        var profile = new Terminal2Profile { Name = "Bench A" };
        profile.Filters[11].Text = "preserved F11";

        var saved = collection.Save(profile);

        Assert.True(saved.IsValid, string.Join(Environment.NewLine, saved.Issues));
        Assert.Equal("preserved F11", profile.Filters[11].Text);
        Assert.Equal(new[] { "Bench A" }, collection.List().Value);

        profile.LogPrefix = "Updated_";
        Assert.True(collection.Save(profile).IsValid);
        Assert.Equal("Updated_", collection.Load("Bench A").Value!.LogPrefix);

        var filePath = Path.Combine(collection.DirectoryPath, "Bench A.profile");
        var contents = File.ReadAllText(filePath);
        File.WriteAllText(filePath, contents.Replace("Name=Bench A\n", "Name=Embedded value\n", StringComparison.Ordinal));

        var loaded = collection.Load("Bench A");

        Assert.True(loaded.IsValid, string.Join(Environment.NewLine, loaded.Issues));
        Assert.Equal("Bench A", loaded.Value!.Name);
        Assert.Equal("preserved F11", loaded.Value.Filters[11].Text);
    }

    [Fact]
    public void Create_writes_a_blank_terminal2_profile_and_refuses_to_replace_an_existing_profile()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        var profile = new Terminal2Profile { Name = "New Bench" };

        var created = collection.Create(profile);
        var original = File.ReadAllText(Path.Combine(collection.DirectoryPath, "New Bench.profile"));
        var collision = collection.Create(new Terminal2Profile { Name = "New Bench" });
        var loaded = collection.Load("New Bench");

        Assert.True(created.IsValid, string.Join(Environment.NewLine, created.Issues));
        Assert.False(collision.IsValid);
        Assert.Contains(collision.Issues, issue => issue.Code == "profile.already-exists");
        Assert.Equal(original, File.ReadAllText(Path.Combine(collection.DirectoryPath, "New Bench.profile")));
        Assert.True(loaded.IsValid, string.Join(Environment.NewLine, loaded.Issues));
        Assert.Equal("New Bench", loaded.Value!.Name);
        Assert.True(loaded.Value.SendCR);
        Assert.Equal(115200, int.Parse(loaded.Value.Baudrate, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Terminal2Profile.MacroCapacity, loaded.Value.Macros.Length);
    }

    [Fact]
    public void Rename_moves_file_without_rewriting_contents_then_delete_removes_it()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Save(new Terminal2Profile { Name = "Old" }).IsValid);
        var oldPath = Path.Combine(collection.DirectoryPath, "Old.profile");
        var originalContents = File.ReadAllText(oldPath);

        var renamed = collection.Rename("Old", "New");

        Assert.True(renamed.IsValid, string.Join(Environment.NewLine, renamed.Issues));
        Assert.Equal("New", renamed.Value);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(originalContents, File.ReadAllText(Path.Combine(collection.DirectoryPath, "New.profile")));
        Assert.Equal("New", collection.Load("New").Value!.Name);
        Assert.Equal(new[] { "New" }, collection.List().Value);

        var deleted = collection.Delete("New");

        Assert.True(deleted.IsValid, string.Join(Environment.NewLine, deleted.Issues));
        Assert.True(deleted.Value);
        Assert.Empty(collection.List().Value!);
        Assert.Equal("profile.not-found", collection.Load("New").Issues.Single().Code);
    }

    [Fact]
    public void Rename_refuses_to_replace_an_existing_profile()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Save(new Terminal2Profile { Name = "First" }).IsValid);
        Assert.True(collection.Save(new Terminal2Profile { Name = "Second" }).IsValid);
        var secondPath = Path.Combine(collection.DirectoryPath, "Second.profile");
        var secondContents = File.ReadAllText(secondPath);

        var renamed = collection.Rename("First", "Second");

        Assert.False(renamed.IsValid);
        Assert.Equal("profile.already-exists", renamed.Issues.Single().Code);
        Assert.True(File.Exists(Path.Combine(collection.DirectoryPath, "First.profile")));
        Assert.Equal(secondContents, File.ReadAllText(secondPath));
    }

    [Fact]
    public void Search_matches_profile_names_case_insensitively_and_empty_query_lists_all()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Save(new Terminal2Profile { Name = "Pump Alpha" }).IsValid);
        Assert.True(collection.Save(new Terminal2Profile { Name = "pump beta" }).IsValid);
        Assert.True(collection.Save(new Terminal2Profile { Name = "Valve" }).IsValid);

        var matches = collection.Search(" PUMP ");
        var all = collection.Search("  ");

        Assert.True(matches.IsValid, string.Join(Environment.NewLine, matches.Issues));
        Assert.Equal(new[] { "Pump Alpha", "pump beta" }, matches.Value);
        Assert.Equal(new[] { "Pump Alpha", "pump beta", "Valve" }, all.Value);
    }

    [Fact]
    public void Copy_loads_and_serializes_a_detached_profile_without_changing_the_source()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        var source = new Terminal2Profile { Name = "Fixture" };
        source.LogPrefix = "Trace_";
        source.Filters[4].Text = "preserved filter";
        Assert.True(collection.Save(source).IsValid);
        var sourcePath = Path.Combine(collection.DirectoryPath, "Fixture.profile");
        var originalSourceContents = File.ReadAllText(sourcePath);

        var copied = collection.Copy("Fixture", "Fixture Copy");

        Assert.True(copied.IsValid, string.Join(Environment.NewLine, copied.Issues));
        Assert.NotSame(source, copied.Value);
        Assert.Equal("Fixture", source.Name);
        Assert.Equal("Fixture Copy", copied.Value!.Name);
        Assert.Equal("Trace_", copied.Value.LogPrefix);
        Assert.Equal("preserved filter", copied.Value.Filters[4].Text);
        Assert.Equal(originalSourceContents, File.ReadAllText(sourcePath));
        Assert.Equal("Fixture Copy", collection.Load("Fixture Copy").Value!.Name);
        Assert.Equal(new[] { "Fixture", "Fixture Copy" }, collection.List().Value);
    }

    [Fact]
    public void Copy_refuses_to_overwrite_an_existing_destination_and_reports_missing_sources()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        Assert.True(collection.Save(new Terminal2Profile { Name = "Source" }).IsValid);
        Assert.True(collection.Save(new Terminal2Profile { Name = "Destination" }).IsValid);
        var destinationPath = Path.Combine(collection.DirectoryPath, "Destination.profile");
        var destinationContents = File.ReadAllText(destinationPath);

        var collision = collection.Copy("Source", "Destination");
        var missing = collection.Copy("Missing", "New copy");
        var invalidName = collection.Copy("Source", "../outside");

        Assert.False(collision.IsValid);
        Assert.Equal("profile.already-exists", collision.Issues.Single().Code);
        Assert.Equal(destinationContents, File.ReadAllText(destinationPath));
        Assert.False(missing.IsValid);
        Assert.Equal("profile.not-found", missing.Issues.Single().Code);
        Assert.False(File.Exists(Path.Combine(collection.DirectoryPath, "New copy.profile")));
        Assert.False(invalidName.IsValid);
        Assert.Equal("profile.invalid-name", invalidName.Issues.Single().Code);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "outside.profile")));
    }

    [Fact]
    public void Failed_serialization_does_not_change_existing_profile_file()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(Path.Combine(temporary.Path, "profiles"));
        var profile = new Terminal2Profile { Name = "Stable" };
        Assert.True(collection.Save(profile).IsValid);
        var path = Path.Combine(collection.DirectoryPath, "Stable.profile");
        var oldContents = File.ReadAllText(path);
        profile.InputFont = "invalid\nfont";

        var result = collection.Save(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.multiline-value");
        Assert.Equal(oldContents, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(collection.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("sub\\profile")]
    [InlineData("C:\\outside")]
    [InlineData("NUL")]
    [InlineData("bad:name")]
    [InlineData("name.")]
    [InlineData("name.profile")]
    public void Unsafe_profile_names_are_rejected_before_file_operations(string name)
    {
        using var temporary = new TemporaryDirectory();
        var directory = Path.Combine(temporary.Path, "profiles");
        var collection = new Terminal2ProfileCollection(directory);
        var outsidePath = Path.Combine(temporary.Path, "outside.profile");
        File.WriteAllText(outsidePath, "leave unchanged");

        var load = collection.Load(name);
        var save = collection.Save(new Terminal2Profile { Name = name });
        var rename = collection.Rename(name, "safe");
        var delete = collection.Delete(name);

        Assert.Equal("profile.invalid-name", load.Issues.Single().Code);
        Assert.Equal("profile.invalid-name", save.Issues.Single().Code);
        Assert.Equal("profile.invalid-name", rename.Issues.Single().Code);
        Assert.Equal("profile.invalid-name", delete.Issues.Single().Code);
        Assert.Equal("leave unchanged", File.ReadAllText(outsidePath));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void List_is_top_level_and_reports_directory_io_errors()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Path.Combine(temporary.Path, "profiles");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "z.profile"), "");
        File.WriteAllText(Path.Combine(directory, "a.profile"), "");
        File.WriteAllText(Path.Combine(directory, "ignored.tmf"), "");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "hidden.profile"), "");

        var listed = new Terminal2ProfileCollection(directory).List();

        Assert.True(listed.IsValid);
        Assert.Equal(new[] { "a", "z" }, listed.Value);

        var fileInsteadOfDirectory = Path.Combine(temporary.Path, "not-a-directory");
        File.WriteAllText(fileInsteadOfDirectory, "file");
        var failed = new Terminal2ProfileCollection(fileInsteadOfDirectory).List();
        Assert.False(failed.IsValid);
        Assert.Equal("profile.directory-not-directory", failed.Issues.Single().Code);
    }

    [Fact]
    public void Save_reports_actionable_filesystem_errors()
    {
        using var temporary = new TemporaryDirectory();
        var pathOccupiedByFile = Path.Combine(temporary.Path, "profiles");
        File.WriteAllText(pathOccupiedByFile, "not a directory");
        var collection = new Terminal2ProfileCollection(pathOccupiedByFile);

        var result = collection.Save(new Terminal2Profile { Name = "New profile" });

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("profile.save-failed", issue.Code);
        Assert.Contains("New profile", issue.Message, StringComparison.Ordinal);
        Assert.Contains(pathOccupiedByFile, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_file_symlink_is_skipped_and_never_followed()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Path.Combine(temporary.Path, "profiles");
        Directory.CreateDirectory(directory);
        var externalPath = Path.Combine(temporary.Path, "outside.profile");
        const string externalContents = "external sentinel";
        File.WriteAllText(externalPath, externalContents);
        var linkPath = Path.Combine(directory, "linked.profile");

        try
        {
            File.CreateSymbolicLink(linkPath, externalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some Windows configurations do not permit unprivileged symbolic links.
            if (new FileInfo(linkPath).LinkTarget is null)
                return;
        }

        Assert.NotNull(new FileInfo(linkPath).LinkTarget);

        var collection = new Terminal2ProfileCollection(directory);
        var list = collection.List();
        var load = collection.Load("linked");
        var save = collection.Save(new Terminal2Profile { Name = "linked" });
        var rename = collection.Rename("linked", "renamed");
        var delete = collection.Delete("linked");

        Assert.DoesNotContain("linked", list.Value!);
        Assert.Contains(list.Issues, issue => issue.Code == "profile.symlink-skipped");
        Assert.Equal("profile.symlink-not-allowed", load.Issues.Single().Code);
        Assert.Equal("profile.symlink-not-allowed", save.Issues.Single().Code);
        Assert.Equal("profile.symlink-not-allowed", rename.Issues.Single().Code);
        Assert.Equal("profile.symlink-not-allowed", delete.Issues.Single().Code);
        Assert.Equal(externalContents, File.ReadAllText(externalPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SignalForge-Terminal2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
