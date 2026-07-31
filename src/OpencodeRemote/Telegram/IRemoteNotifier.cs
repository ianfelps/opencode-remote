using OpencodeRemote.Telegram.Models;

namespace OpencodeRemote.Telegram;

public interface IRemoteNotifier
{
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken);
    Task UpdateProgressAsync(long chatId, string text, CancellationToken cancellationToken);
    Task ClearProgressAsync(long chatId, CancellationToken cancellationToken);
    Task StartTypingAsync(long chatId, CancellationToken cancellationToken);
    Task StopTypingAsync(long chatId);
    Task SendPermissionAsync(long chatId, string directory, string sessionId, string permissionId, string title, bool useV2, CancellationToken cancellationToken);
    Task SendQuestionAsync(long chatId, string directory, PendingQuestion question, bool useV2, CancellationToken cancellationToken);
    Task SendPlanReadyAsync(long chatId, string directory, string sessionId, CancellationToken cancellationToken);
}
