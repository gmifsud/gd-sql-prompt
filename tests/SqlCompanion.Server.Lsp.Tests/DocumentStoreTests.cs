using SqlCompanion.Engine;

namespace SqlCompanion.Server.Lsp.Tests;

public sealed class DocumentStoreTests
{
    private const string Uri = "file:///a.sql";

    [Fact]
    public void TryUpdate_RejectsOlderOrEqualVersions()
    {
        var store = new DocumentStore();
        Assert.True(store.TryUpdate(new DocumentSnapshot(Uri, 2, "v2")));
        Assert.False(store.TryUpdate(new DocumentSnapshot(Uri, 1, "v1")));
        Assert.False(store.TryUpdate(new DocumentSnapshot(Uri, 2, "v2-dup")));

        Assert.True(store.TryGet(Uri, out var current));
        Assert.Equal("v2", current.Text);
    }

    [Fact]
    public void IsCurrent_IsFalseOnceSuperseded()
    {
        var store = new DocumentStore();
        var v1 = new DocumentSnapshot(Uri, 1, "a");
        store.TryUpdate(v1);
        Assert.True(store.IsCurrent(v1));

        store.TryUpdate(new DocumentSnapshot(Uri, 2, "b"));
        Assert.False(store.IsCurrent(v1));
    }

    [Fact]
    public void TryUpdate_IsMonotonicUnderConcurrency()
    {
        var store = new DocumentStore();
        Parallel.For(1, 1_001, v => store.TryUpdate(new DocumentSnapshot(Uri, v, v.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.True(store.TryGet(Uri, out var current));
        Assert.Equal(1_000, current.Version);
    }
}
