using ChizuChan.DTOs;
using NetCord.Rest;
using NetCord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services
{
    public class EmbedService : IEmbedService
    {
        private readonly IPlexService _plexService;

        public EmbedService(IPlexService plexService)
        {
            _plexService = plexService;
        }

        public (EmbedProperties Embed, IComponentProperties[] Components) BuildSearchEmbed(ResultDTO record, int index, int total, int page = 1, int totalPages = 1)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            string downloadStatus = _plexService.GetDownloadStatus(record);

            string title = string.IsNullOrWhiteSpace(record.Title) ? record.Name ?? "Unknown Title" : record.Title;
            string description = string.IsNullOrWhiteSpace(record.Overview) ? "No description available." : record.Overview;
            string originalName = record.OriginalName ?? record.OriginalTitle ?? "Unknown";
            string firstAired = string.IsNullOrWhiteSpace(record.FirstAirDate) ? "Unknown" : record.FirstAirDate;
            string popularity = record.Popularity.HasValue ? record.Popularity.Value.ToString("0.00") : "Unknown";

            string? posterUrl = string.IsNullOrWhiteSpace(record.PosterPath) ? null : $"https://image.tmdb.org/t/p/w500{record.PosterPath}";

            bool isDownloaded = record.MediaInfo != null && record.MediaInfo.TmdbId > 0;
            int tmdbId = isDownloaded ? record.MediaInfo.TmdbId : record.Id;

            string idLabel = isDownloaded ? "TMDB ID" : "ID";

            List<EmbedFieldProperties> fields = new List<EmbedFieldProperties>
            {
                new EmbedFieldProperties { Name = "Original Name:", Value = originalName, Inline = false },
                new EmbedFieldProperties { Name = "First Aired:", Value = firstAired, Inline = false },
                new EmbedFieldProperties { Name = "Popularity:", Value = popularity, Inline = false },
                new EmbedFieldProperties { Name = "Library Status:", Value = downloadStatus, Inline = false }
            };

            if (!string.IsNullOrWhiteSpace(record.MediaInfo?.PlexUrl))
            {
                fields.Add(new EmbedFieldProperties
                {
                    Name = "Watch on Plex:",
                    Value = $"[Click here]({record.MediaInfo.PlexUrl})",
                    Inline = false
                });
            }

            EmbedProperties embed = new EmbedProperties
            {
                Title = title.Length > 256 ? title[..256] : title,
                Description = description.Length > 2048 ? description[..2048] : description,
                Thumbnail = posterUrl,
                Color = new Color(0xf7df47),
                Fields = fields.ToArray(),
                Footer = new EmbedFooterProperties
                {
                    Text = $"Result {index + 1} of {total}  •  Page {page} of {totalPages}  •  {idLabel}: {tmdbId}"
                }
            };

            IComponentProperties[] components = new IComponentProperties[]
            {
                new ActionRowProperties
                {
                    new ButtonProperties("previous_button", "⏮️ Prev Result", ButtonStyle.Primary),
                    new ButtonProperties("next_button", "⏭️ Next Result", ButtonStyle.Primary),
                    new ButtonProperties("select_button", "⏬ Download Result", ButtonStyle.Success)
                }
            };

            return (embed, components);
        }

        public ModalProperties BuildSearchModal(ResultDTO record, int index, int page)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));


            string customId = $"search_modal_{record.Id}_{index}_{page}";
            string title = $"Details for: {record.Title ?? record.Name ?? "Unknown"}";

            List<IComponentProperties> components = new List<IComponentProperties>
            {
                new TextDisplayProperties($"{record.Title} ?? {record.Name} ?? 'N/A''"),
                new TextDisplayProperties($"{record.Overview} ?? 'No overview available''")
            };

            ModalProperties modal = new ModalProperties(
                    customId: customId,
                    title: title,
                    components: components
                );

            return modal;
        }

        public (EmbedProperties Embed, IComponentProperties[] Components) BuildMusicPlayerEmbed(string title, string? sourceUrl, User requestedBy, bool isPaused, bool canSkip, TimeSpan? position = null, TimeSpan? duration = null, string? thumbnailUrl = null)
        {
            string safeTitle = string.IsNullOrWhiteSpace(title) ? "Now Playing" : title;
            string desc = sourceUrl is not null ? $"Source: {sourceUrl}" : string.Empty;

            // Optional Time Line
            string timeline = string.Empty;
            if (position.HasValue || duration.HasValue)
            {
                string pos = position.HasValue ? FormatTime(position.Value) : "--:--";
                string dur = duration.HasValue ? FormatTime(duration.Value) : "--:--";
                timeline = $"\n\nTime: {pos} / {dur}";
            }

            var embed = new EmbedProperties
            {
                Title = safeTitle.Length > 256 ? safeTitle[..256] : safeTitle,
                Description = (desc + timeline).Trim(),
                Thumbnail = thumbnailUrl is null ? null : new EmbedThumbnailProperties(thumbnailUrl),
                Color = new Color(0x5865F2),
                Footer = new EmbedFooterProperties { Text = $"Requested by {requestedBy.Username}" }
            };

            // Row with four explicit controls. Toggle disabled pair based on paused status
            var row = new ActionRowProperties
            {
                new ButtonProperties("music_skip", "Skip", ButtonStyle.Primary) { Disabled = !canSkip }, // <-- toggle
                new ButtonProperties("music_pause", "Pause", ButtonStyle.Secondary) { Disabled = isPaused },
                new ButtonProperties("music_resume", "Resume", ButtonStyle.Secondary) { Disabled = !isPaused },
                new ButtonProperties("music_stop", "Stop", ButtonStyle.Danger),
            };

            return (embed, new IComponentProperties[] { row });
        }

        public EmbedProperties BuildQueuedConfirmationEmbed(string displayTitle, string? sourceUrl, User requestedBy)
        {
            bool titleIsUrl = !string.IsNullOrWhiteSpace(sourceUrl) &&
                              string.Equals(displayTitle, sourceUrl, StringComparison.OrdinalIgnoreCase);

            string description = titleIsUrl
                ? sourceUrl!                 
                : (sourceUrl is null ? displayTitle : $"{displayTitle}\n{sourceUrl}");

            return new EmbedProperties
            {
                Title = "Queued",
                Description = description,
                Color = new Color(0x57F287),
                Footer = new EmbedFooterProperties { Text = $"Requested by {requestedBy.Username}" }
            };
        }

        private static string FormatTime(TimeSpan ts)
            => $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }
}
