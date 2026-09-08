using ChizuChan.Services;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands;

public sealed class YouTubeLongMediaStatusModule(YouTubeLongMediaStore store)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("youtube_job", "Check your durable YouTube download job status.",
        Contexts = [InteractionContextType.BotDMChannel])]
    public async Task StatusAsync(
        [SlashCommandParameter(Name = "id", Description = "Job ID, or omit for your latest job")]
        string? id = null)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());
        var content = Context.Guild is not null ? "This command can only be used in DMs." : FormatStatus(Context.User.Id, id, store);
        await ModifyResponseAsync(message =>
        {
            message.Content = content;
            message.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    public static string FormatStatus(ulong owner, string? id, YouTubeLongMediaStore store)
    {
        var job = store.GetOwned(owner, id?.Trim());
        if (job is null) return "No matching YouTube job was found for your account.";
        var delivery = job.DeliveredMessageId is not null ? "DM delivered." : job.DeliveryError ?? "Result DM pending.";
        return $"YouTube job `{job.Id}`: {job.State}.\n{job.Message}\n{delivery}";
    }
}
