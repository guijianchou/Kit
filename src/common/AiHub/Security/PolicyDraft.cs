namespace Kit.AiHub.Security;

/// <summary>Keeps one editor draft independent of selection, reloads and asynchronous saves.</summary>
public sealed class PolicyDraft
{
    private string _savedContent = string.Empty;
    private bool _editedBeforeLoad;

    public string Content { get; private set; } = string.Empty;

    public bool IsLoaded { get; private set; }

    public bool IsDirty => IsLoaded ? !EquivalentEditorText(Content, _savedContent) : _editedBeforeLoad;

    public bool SetContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (EquivalentEditorText(Content, content))
        {
            // WinUI's multiline TextBox normalizes line endings when displaying text and
            // can write that value back through a TwoWay binding. Keep the original bytes
            // represented by the draft when the editor made no textual change.
            return false;
        }

        Content = content;
        _editedBeforeLoad = !IsLoaded;
        return true;
    }

    public void Load(string savedContent)
    {
        ArgumentNullException.ThrowIfNull(savedContent);
        bool preserveDraft = IsDirty;
        _savedContent = savedContent;
        IsLoaded = true;
        if (!preserveDraft)
        {
            Content = savedContent;
        }

        _editedBeforeLoad = false;
    }

    public void AcceptSaved(string savedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(savedSnapshot);
        _savedContent = savedSnapshot;
        IsLoaded = true;
        _editedBeforeLoad = false;
        // A newer edit made while saving remains a draft; only the saved snapshot is clean.
    }

    public void Discard()
    {
        if (IsLoaded)
        {
            Content = _savedContent;
            _editedBeforeLoad = false;
        }
    }

    private static bool EquivalentEditorText(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal) ||
        string.Equals(NormalizeEditorNewlines(left), NormalizeEditorNewlines(right), StringComparison.Ordinal);

    private static string NormalizeEditorNewlines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
