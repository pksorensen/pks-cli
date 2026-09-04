namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// One file to hang on a draft. Graph wants the bytes inline as base64 for anything
/// small, which is every invoice PDF and letter attachment we send.
/// </summary>
public class MsGraphDraftAttachment
{
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// A message to be composed into the Drafts folder. There is deliberately no send
/// counterpart: this CLI composes mail, a human presses Send.
/// </summary>
public class MsGraphDraftRequest
{
    public string Subject { get; set; } = string.Empty;

    /// <summary>HTML body. Graph stores one body per message; Outlook derives the plain-text alternative when the message is sent.</summary>
    public string HtmlBody { get; set; } = string.Empty;

    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();

    /// <summary>Reply-to address, when the draft should be answered somewhere other than the sending mailbox.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Internet message id of the mail this draft answers, angle brackets and all
    /// (<c>&lt;AS8PR…@…outlook.com&gt;</c>). When set, the draft is created with Graph's
    /// createReply so it carries the conversation headers and the quoted history — a mail
    /// client threads on those, never on a subject that happens to start with "SV:".
    /// Leave null for a mail that opens a new thread.
    /// </summary>
    public string? InReplyTo { get; set; }

    public List<MsGraphDraftAttachment> Attachments { get; set; } = new();
}

/// <summary>
/// What Graph gave back after the draft was created.
/// </summary>
public class MsGraphDraftResult
{
    public string Id { get; set; } = string.Empty;
    public string? WebLink { get; set; }
    public int AttachmentCount { get; set; }
}

/// <summary>
/// The little of a message that identity-matching needs: which subject, to whom, and when
/// it left. Subjects are not unique — three letters in a batch can share one — so anything
/// that asks "is this letter already in the mailbox?" has to look at the recipients too.
/// </summary>
public class MsGraphFolderMessage
{
    public string Subject { get; set; } = string.Empty;
    public List<string> To { get; set; } = new();
    public DateTime? SentDateTime { get; set; }
    public string? WebLink { get; set; }
}
