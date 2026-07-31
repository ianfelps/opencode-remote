namespace OpencodeRemote.Telegram.Models;

public sealed record PendingQuestion(string Id, string SessionId, IReadOnlyList<QuestionPrompt> Questions);

public sealed record QuestionPrompt(string Question, string Header, bool Multiple, IReadOnlyList<QuestionOption> Options);

public sealed record QuestionOption(string Label, string Description);
