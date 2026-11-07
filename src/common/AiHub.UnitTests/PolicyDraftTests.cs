namespace Kit.AiHub.UnitTests;

using Kit.AiHub.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PolicyDraftTests
{
    [TestMethod]
    public void LoadingDoesNotOverwriteAnEditMadeBeforeTheReadCompletes()
    {
        var draft = new PolicyDraft();
        draft.SetContent("typed while loading");
        draft.Load("saved policy");

        Assert.IsTrue(draft.IsLoaded);
        Assert.IsTrue(draft.IsDirty);
        Assert.AreEqual("typed while loading", draft.Content);
        draft.Discard();
        Assert.AreEqual("saved policy", draft.Content);
        Assert.IsFalse(draft.IsDirty);
    }

    [TestMethod]
    public void DraftsRemainIndependentAcrossSelectionAndReload()
    {
        var audit = new PolicyDraft();
        var optimization = new PolicyDraft();
        audit.Load("audit saved");
        optimization.Load("optimization saved");
        PolicyDraft selected = audit;
        selected.SetContent("audit draft");
        selected = optimization;
        selected.SetContent("optimization draft");

        audit.Load("audit refreshed on disk");
        optimization.Load("optimization saved");
        Assert.AreEqual("audit draft", audit.Content);
        Assert.AreEqual("optimization draft", optimization.Content);
        Assert.IsTrue(audit.IsDirty);
        Assert.IsTrue(optimization.IsDirty);
        optimization.AcceptSaved("optimization draft");
        Assert.IsFalse(optimization.IsDirty);
        Assert.IsTrue(audit.IsDirty);
        audit.Discard();
        Assert.AreEqual("audit refreshed on disk", audit.Content);
    }

    [TestMethod]
    public void CompletedSaveAcknowledgesOnlyItsSnapshot()
    {
        var draft = new PolicyDraft();
        draft.Load("saved policy");
        draft.SetContent("first edit");
        string saveSnapshot = draft.Content;
        draft.SetContent("new edit while saving");
        draft.AcceptSaved(saveSnapshot);

        Assert.IsTrue(draft.IsDirty);
        Assert.AreEqual("new edit while saving", draft.Content);
        draft.Discard();
        Assert.AreEqual("first edit", draft.Content);
        Assert.IsFalse(draft.IsDirty);
    }

    [TestMethod]
    public void DefaultTemplateIsStagedAndCanBeDiscardedBeforeSaving()
    {
        var draft = new PolicyDraft();
        draft.Load("custom policy");
        draft.SetContent(SelfTestPolicy.Instructions);
        Assert.IsTrue(draft.IsDirty);
        draft.Discard();
        Assert.AreEqual("custom policy", draft.Content);
        draft.SetContent(SelfTestPolicy.Instructions);
        draft.AcceptSaved(SelfTestPolicy.Instructions);
        Assert.IsFalse(draft.IsDirty);
        Assert.IsFalse(draft.SetContent(SelfTestPolicy.Instructions));
    }

    [TestMethod]
    [DataRow("\r\n", "\r")]
    [DataRow("\n", "\r")]
    [DataRow("\r\n", "\n")]
    public void TextBoxNewlineWritebackDoesNotCreateAnEditOrChangeSavedText(string diskNewline, string editorNewline)
    {
        string saved = "first" + diskNewline + "second";
        var draft = new PolicyDraft();
        draft.Load(saved);

        Assert.IsFalse(draft.SetContent("first" + editorNewline + "second"));
        Assert.AreEqual(saved, draft.Content, "Displaying text must preserve the original newline representation.");
        Assert.IsFalse(draft.IsDirty);

        draft.SetContent("changed" + editorNewline + "second");
        Assert.IsTrue(draft.IsDirty);
        draft.Discard();
        Assert.IsFalse(draft.SetContent("first" + editorNewline + "second"));
        Assert.IsFalse(draft.IsDirty, "Discard must remain clean after the TextBox writes its displayed value back.");
        Assert.AreEqual(saved, draft.Content);
    }

    [TestMethod]
    public void NewlineNormalizationDoesNotHideActualEditsOrEraseOtherDrafts()
    {
        var audit = new PolicyDraft();
        var selfTest = new PolicyDraft();
        audit.Load("saved\r\naudit");
        selfTest.Load("saved\nself-test");
        audit.SetContent("saved\raudit\r<!-- draft -->");
        string edited = audit.Content;
        selfTest.SetContent("saved\rself-test");
        Assert.IsFalse(selfTest.IsDirty);
        Assert.IsFalse(audit.SetContent("saved\naudit\n<!-- draft -->"));
        Assert.AreEqual(edited, audit.Content);
        Assert.IsTrue(audit.IsDirty);
        audit.SetContent("saved\naudit");
        Assert.IsFalse(audit.IsDirty, "Undoing the text edit should be clean regardless of the editor's newline convention.");
    }
}
