using System.Collections.Concurrent;
using SqlCompanion.Engine;

namespace SqlCompanion.Server.Lsp;

/// <summary>
/// Holds the latest snapshot per document. Updates are monotonic in version: an out-of-order
/// (older) update is rejected, and results computed from a superseded snapshot can be detected
/// with <see cref="IsCurrent"/> and discarded before publishing.
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, DocumentSnapshot> _documents = new(StringComparer.Ordinal);

    public bool TryGet(string uri, out DocumentSnapshot snapshot) => _documents.TryGetValue(uri, out snapshot!);

    /// <summary>Stores <paramref name="snapshot"/> if it is newer than the current one. Returns false if rejected.</summary>
    public bool TryUpdate(DocumentSnapshot snapshot)
    {
        while (true)
        {
            if (!_documents.TryGetValue(snapshot.Uri, out var current))
            {
                if (_documents.TryAdd(snapshot.Uri, snapshot))
                {
                    return true;
                }

                continue;
            }

            if (snapshot.Version <= current.Version)
            {
                return false;
            }

            if (_documents.TryUpdate(snapshot.Uri, snapshot, current))
            {
                return true;
            }
        }
    }

    public void Remove(string uri) => _documents.TryRemove(uri, out _);

    public bool IsCurrent(DocumentSnapshot snapshot) =>
        _documents.TryGetValue(snapshot.Uri, out var current) && current.Version == snapshot.Version;
}
