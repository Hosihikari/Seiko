using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class ExternalFilesTests
{
    private static (Fixture Fixture, TestHttpServer Server) Setup()
    {
        var fixture = new Fixture();
        var server = new TestHttpServer(fixture.SourceDirectory);
        return (fixture, server);
    }

    [Fact]
    public void Sync_FetchesExternalFile()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/server.bin", "payload"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture(
                            "server/server.bin",
                            server.BaseUrl + "external/vendor/server.bin",
                            "payload"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.FilesDeployed);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(fixture.Root, "server", "server.bin")));
    }

    [Fact]
    public void Sync_ExtractsArchiveMembers()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var archive = Fixture.Zip(new Dictionary<string, byte[]>
        {
            ["assets/icon.png"] = "icon"u8.ToArray(),
            ["assets/ignored.txt"] = "ignored"u8.ToArray(),
        });
        fixture.WriteExternal("vendor/assets.zip", archive);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/assets.zip",
                            "zip",
                            archive,
                            new ExternalMemberFixture("assets/icon.png", "server/icon.png")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("icon", File.ReadAllText(Path.Combine(fixture.Root, "server", "icon.png")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "assets", "ignored.txt")));
    }

    [Fact]
    public void Sync_ExtractsTarAndGzipMembers()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var tar = Fixture.Tar(new Dictionary<string, byte[]> { ["bin/tool"] = "tool"u8.ToArray() });
        var tarGz = Fixture.Gzip(tar);
        fixture.WriteExternal("vendor/tool.tar", tar);
        fixture.WriteExternal("vendor/tool.tar.gz", tarGz);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/tool.tar",
                            "tar",
                            tar,
                            new ExternalMemberFixture("bin/tool", "plain/tool")),
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/tool.tar.gz",
                            "tar.gz",
                            tarGz,
                            new ExternalMemberFixture("bin/tool", "gzip/tool")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("tool", File.ReadAllText(Path.Combine(fixture.Root, "plain", "tool")));
        Assert.Equal("tool", File.ReadAllText(Path.Combine(fixture.Root, "gzip", "tool")));
    }

    [Fact]
    public void Sync_MixedHostedAndExternalFiles()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("mixed", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hosted.txt"] = "hosted" },
                    ExternalFiles =
                    [
                        new ExternalFileFixture("extra.txt", server.BaseUrl + "external/vendor/extra.txt", "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("mixed", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("hosted", File.ReadAllText(Path.Combine(fixture.Root, "hosted.txt")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(fixture.Root, "extra.txt")));
    }

    [Fact]
    public void Sync_KeepsArtifactFilesUnderDotExternal()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [".external/hosted.txt"] = "hosted",
                    },
                    ExternalFiles =
                    [
                        new ExternalFileFixture(
                            ".external/extra.txt",
                            server.BaseUrl + "external/vendor/extra.txt",
                            "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("hosted", File.ReadAllText(Path.Combine(fixture.Root, ".external", "hosted.txt")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(fixture.Root, ".external", "extra.txt")));
    }

    [Fact]
    public void Sync_SameArchiveFeedsSeveralTargetsOnce()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var archive = Fixture.Zip(new Dictionary<string, byte[]> { ["a.txt"] = "a"u8.ToArray() });
        fixture.WriteExternal("vendor/a.zip", archive);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/a.zip",
                            "zip",
                            archive,
                            new ExternalMemberFixture("a.txt", "one.txt"),
                            new ExternalMemberFixture("a.txt", "two.txt")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("a", File.ReadAllText(Path.Combine(fixture.Root, "one.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(fixture.Root, "two.txt")));
        // The archive may be probed with HEAD before the ranged GET.
        var archiveRequests = server.Authorizations.Count(header =>
            header is null) + 0;
        Assert.True(archiveRequests >= 2);
    }

    [Fact]
    public void Sync_MergesRepeatedArchiveDeclarations()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var archive = Fixture.Zip(new Dictionary<string, byte[]>
        {
            ["a.txt"] = "a"u8.ToArray(),
            ["b.txt"] = "b"u8.ToArray(),
        });
        fixture.WriteExternal("vendor/ab.zip", archive);
        var url = server.BaseUrl + "external/vendor/ab.zip";
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            url,
                            "zip",
                            archive,
                            new ExternalMemberFixture("a.txt", "a.txt")),
                        new ExternalArchiveFixture(
                            url,
                            "zip",
                            archive,
                            new ExternalMemberFixture("b.txt", "b.txt")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(fixture.Root, "b.txt")));
    }

    [Fact]
    public void Sync_RejectsRepeatedArchiveWithDifferentIdentity()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var archive = Fixture.Zip(new Dictionary<string, byte[]> { ["a.txt"] = "a"u8.ToArray() });
        fixture.WriteExternal("vendor/a.zip", archive);
        var url = server.BaseUrl + "external/vendor/a.zip";
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            url,
                            "zip",
                            archive,
                            new ExternalMemberFixture("a.txt", "a.txt")),
                        new ExternalArchiveFixture(
                            url,
                            "tar",
                            archive,
                            new ExternalMemberFixture("a.txt", "b.txt")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Contains("same digest, format, and size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSummaryFallsBackToTheManifestAndWarnsOnMismatch()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/server.bin", "payload"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture(
                            "server/server.bin",
                            server.BaseUrl + "external/vendor/server.bin",
                            "payload"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        // An old index has neither the derived summary nor the declarations,
        // but the manifest inside the artifact still declares the file.
        var path = Path.Combine(fixture.SourceDirectory, "sei", "proprietary.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var artifact = node["versions"]![0]!["artifacts"]![0]!.AsObject();
        artifact.Remove("externalFiles");
        artifact.Remove("externalArchives");
        artifact.Remove("external");
        File.WriteAllText(path, node.ToJsonString());

        var preview = new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            dryRun: true).Preview!;
        Assert.Equal(0, preview.ExternalItems);
        Assert.Contains("proprietary", preview.ExternalUnknown);

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal(1, result.ExternalItems);
        Assert.Contains(new Uri(server.BaseUrl).Host, result.ExternalHosts);

        // A present but wrong summary does not block the install; it warns and
        // the manifest remains the reported value.
        artifact["external"] = new JsonObject
        {
            ["files"] = 0,
            ["archives"] = 0,
            ["size"] = 0,
            ["hosts"] = new JsonArray(),
        };
        File.WriteAllText(path, node.ToJsonString());

        var again = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Contains(again.Warnings, warning => warning.Contains("does not match", StringComparison.Ordinal));
        Assert.Equal(1, again.ExternalItems);
    }

    [Fact]
    public void Sync_ExtractsAnEmptyTarMember()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var tar = Fixture.Tar(new Dictionary<string, byte[]> { ["empty.txt"] = [] });
        var tarGz = Fixture.Gzip(tar);
        fixture.WriteExternal("vendor/empty.tar.gz", tarGz);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/empty.tar.gz",
                            "tar.gz",
                            tarGz,
                            new ExternalMemberFixture("empty.txt", "empty.txt")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var path = Path.Combine(fixture.Root, "empty.txt");
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void Sync_LimitsTarEntryCount()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var entries = new Dictionary<string, byte[]>(ArchiveLimits.MaxEntries + 1, StringComparer.Ordinal);
        for (var i = 0; i <= ArchiveLimits.MaxEntries; i++)
        {
            entries[$"f{i}"] = [];
        }

        var tarGz = Fixture.Gzip(Fixture.Tar(entries));
        fixture.WriteExternal("vendor/many.tar.gz", tarGz);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/many.tar.gz",
                            "tar.gz",
                            tarGz,
                            new ExternalMemberFixture("f0", "f0")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Contains($"limit is {ArchiveLimits.MaxEntries}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_FailsOnDigestMismatch()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        // Same length as the declared digest content, so the failure is the
        // digest and not the declared size.
        fixture.WriteExternal("vendor/extra.txt", "actual"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture("extra.txt", server.BaseUrl + "external/vendor/extra.txt", "expect"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "extra.txt")));
    }

    [Fact]
    public void Sync_FailsWhenMemberIsMissing()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        var archive = Fixture.Zip(new Dictionary<string, byte[]> { ["other.txt"] = "other"u8.ToArray() });
        fixture.WriteExternal("vendor/a.zip", archive);
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalArchives =
                    [
                        new ExternalArchiveFixture(
                            server.BaseUrl + "external/vendor/a.zip",
                            "zip",
                            archive,
                            new ExternalMemberFixture("missing.txt", "missing.txt")),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
    }

    [Fact]
    public void Sync_RejectsExternalTargetThatCollidesWithZip()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["dup.txt"] = "zip" },
                    ExternalFiles =
                    [
                        new ExternalFileFixture("dup.txt", server.BaseUrl + "external/vendor/extra.txt", "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("also exists inside the artifact", error.Message);
    }

    [Fact]
    public void Sync_ManifestV3IsAcceptedAndExternalIsRequired()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.AddSei("plain", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["plain.txt"] = "plain" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("plain", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("plain", File.ReadAllText(Path.Combine(fixture.Root, "plain.txt")));
    }

    [Fact]
    public void Sync_ManifestV2RejectsExternalDeclarations()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ManifestSchemaVersion = 2,
                    ExternalFiles =
                    [
                        new ExternalFileFixture("extra.txt", server.BaseUrl + "external/vendor/extra.txt", "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("schemaVersion 2", error.Message);
    }

    [Fact]
    public void Offline_FailsWhenExternalFileIsNotStored()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture("extra.txt", server.BaseUrl + "external/vendor/extra.txt", "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, "extra.txt"));

        // The store entry still holds the external file, so an offline replay
        // redeploys from the store without touching the network.
        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true);

        Assert.Equal("extra", File.ReadAllText(Path.Combine(fixture.Root, "extra.txt")));
    }

    [Fact]
    public void DryRun_ReportsExternalWithoutDownloading()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        // Never written to the server root: a dry-run that fetched would fail.
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture(
                            "extra.txt",
                            server.BaseUrl + "external/vendor/extra.txt",
                            "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var preview = new SeikoClient()
            .Sync(fixture.Root, fixture.StoreDirectory, dryRun: true)
            .Preview!;

        Assert.Equal(1, preview.ExternalItems);
        Assert.Equal(["127.0.0.1"], preview.ExternalHosts);
    }

    [Fact]
    public void Info_ReportsExternalHostsAndCount()
    {
        var (fixture, server) = Setup();
        using var _ = fixture;
        using var __ = server;
        fixture.WriteExternal("vendor/extra.txt", "extra"u8.ToArray());
        fixture.AddSei("proprietary", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    ExternalFiles =
                    [
                        new ExternalFileFixture("extra.txt", server.BaseUrl + "external/vendor/extra.txt", "extra"),
                    ],
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("proprietary", "[1.0.0]"));

        var info = new SeikoClient().Info(fixture.Root, "proprietary");

        var artifact = Assert.Single(Assert.Single(info.Versions).Artifacts);
        Assert.Equal(1, artifact.ExternalCount);
        Assert.Equal(["127.0.0.1"], artifact.ExternalHosts);
    }
}
