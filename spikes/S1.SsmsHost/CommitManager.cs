using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// Ends our completion session at the end of a word. Without a commit manager the session had no commit
/// characters, so it outlived the word it started on: typing <c>SELECT @</c> in one go kept the session opened at
/// <c>S</c>, its filter text became <c>SELECT @</c>, and only <c>SELECT</c> matched — <c>@variable</c> items were
/// filtered out until a pause or Esc let <c>@</c> open a fresh session.
/// </summary>
[Export(typeof(IAsyncCompletionCommitManagerProvider))]
[ContentType("SQL")]
[Name("SqlCompanion.S1.CommitManager")]
internal sealed class CommitManagerProvider : IAsyncCompletionCommitManagerProvider
{
    public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new CommitManager());
}

internal sealed class CommitManager : IAsyncCompletionCommitManager
{
    // '@', '#', '_' and '$' are identifier characters in T-SQL and stay out. Tab and Enter are commands, not typed
    // characters: they always commit.
    private static readonly char[] WordEnders =
    [
        ' ', '.', ',', ';', '(', ')', '[', ']', '=', '<', '>', '+', '-', '*', '/', '%', '\'', '"', '!', '&', '|', '^', '~', ':',
    ];

    public IEnumerable<char> PotentialCommitCharacters => WordEnders;

    // Only our sessions: in Off / Merge our source does not participate, so any other async session is left alone.
    public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) =>
        CompletionModeSettings.Mode == CompletionMode.Replace;

    public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
    {
        if (item.Source is not CompletionSource || typedChar is '\0' or '\t' or '\n' or '\r')
        {
            return CommitResult.Unhandled; // Tab, Enter, double-click: the editor inserts the selected item
        }

        // A word-ending character closes the session without inserting the selection, so a half-typed alias such
        // as "t." never becomes "TABLE."; the character itself is still typed. Commit-on-space as in SSMS is a
        // Phase 2 option (CMP), not a spike default.
        CompletionStats.Note("session ended by a word-ending character (selection not inserted)");
        return CommitResult.Handled;
    }
}
